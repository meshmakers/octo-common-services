using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     <see cref="ICkMethodDispatcher" />: resolves the declaring element, prefers the in-process transport when the
///     method is hosted in this process and otherwise supplies the bus <c>send</c> as the terminal delegate of
///     <see cref="ICkMethodInvocationPipeline" />. Filters (audit, authorization, idempotency) are the pipeline's job.
/// </summary>
internal sealed class CkMethodDispatcher(
    ICkMethodDeclaringElementResolver resolver,
    ICkMethodInvocationPipeline pipeline,
    CkMethodHandlerRegistry registry,
    IDistributionEventHubService eventHub,
    IServiceProvider serviceProvider,
    ICkMethodSensitivityResolver sensitivity,
    IOptions<CkMethodDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<CkMethodDispatcher> logger) : ICkMethodDispatcher
{
    internal const string ReasonNoLiveHandler = "no-live-handler";
    internal const string ReasonTenantNotServed = "tenant-not-served";

    public async Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var methodId = resolver.Resolve(request);
            if (!string.Equals(methodId, request.MethodId, StringComparison.Ordinal))
            {
                request = request with { MethodId = methodId };
            }

            // Clamp first, before any transport, filter or send: from here on nothing can overflow for an absurd
            // deadline (DateTime.MaxValue) and the in-process and the bus path see the same request.
            request = NormalizeTimeout(request);

            // A method registered in-process is found before any registry lookup; the in-process transport runs the
            // same pipeline itself (transport name "in-process").
            var inProcess = serviceProvider.GetService(typeof(ICkInProcessMethodTransport)) as ICkInProcessMethodTransport;
            if (inProcess != null && inProcess.CanHandle(request.MethodId))
            {
                return await inProcess.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }

            return await pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request,
                r => SendAsync(r, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The contract is "never throws for a failing call"; the pipeline and the transports already answer their own
            // failures, this is the last line for the rest (resolver, DI).
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(Redactor(request).Redact(e), "CK method {MethodId} invocation {InvocationId} failed in the dispatcher (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>
    ///     The deadline is at most now + <see cref="CkMethodDispatcherOptions.MaxTimeout" /> (also the deadline the handler
    ///     sees); <see cref="CkMethodRequest.TimeoutSeconds" /> is filled from the deadline when the caller left it out.
    /// </summary>
    private CkMethodRequest NormalizeTimeout(CkMethodRequest request)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var max = CkMethodDeadline.MaxOf(options.Value);
        var deadline = CkMethodDeadline.Clamp(request.DeadlineUtc, now, max);
        var seconds = request.TimeoutSeconds is { } given
            ? CkMethodDeadline.ClampSeconds(given)
            : CkMethodDeadline.ClampSeconds((int)Math.Ceiling(Math.Max((deadline - now).TotalSeconds, 0)));
        return deadline == request.DeadlineUtc && seconds == request.TimeoutSeconds
            ? request
            : request with { DeadlineUtc = deadline, TimeoutSeconds = seconds };
    }

    /// <summary>The bus transport: the terminal delegate of the pipeline.</summary>
    private async Task<CkMethodResponse> SendAsync(CkMethodRequest request, CancellationToken cancellationToken)
    {
        var sendStartedUtc = timeProvider.GetUtcNow().UtcDateTime;
        var opt = options.Value;
        var maxTimeout = CkMethodDeadline.MaxOf(opt);
        var grace = opt.ResponseGrace > TimeSpan.Zero ? opt.ResponseGrace : TimeSpan.Zero;

        // Clamp first: nothing below can overflow or throw for an absurd deadline (DateTime.MaxValue, TimeSpan.MaxValue).
        var remaining = request.DeadlineUtc - sendStartedUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return Timeout(request);
        }

        if (remaining > maxTimeout)
        {
            remaining = maxTimeout;
            request = request with { DeadlineUtc = sendStartedUtc + maxTimeout };
        }

        var live = registry.GetLiveHandlers(request.MethodId);
        if (live.Count > 0)
        {
            var serving = live.Where(e => CkMethodHandlerRegistry.Serves(e, request.TenantId)).ToList();
            if (serving.Count == 0)
            {
                return NotAvailable(request, ReasonTenantNotServed);
            }

            // Wire contract rule 4: never send a newer contract version than the handler announced.
            var supported = serving.Max(e => e.ContractVersion);
            if (request.ContractVersion > supported)
            {
                return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                    CkMethodError.Create(CkMethodErrorCodes.MethodContractVersionUnsupported,
                        (CkMethodErrorParameters.RequestedVersion, request.ContractVersion.ToString()),
                        (CkMethodErrorParameters.SupportedVersion, supported.ToString())));
            }
        }

        // The registry is advisory: an empty registry (dispatcher just started, heartbeat lost) must not refuse a call
        // that the broker can still deliver. The mandatory flag is the truth.
        var wait = remaining + grace;
        try
        {
            return await eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(
                CkMethodQueueNames.For(request.MethodId), request,
                new CommandSendOptions
                {
                    Timeout = wait,
                    Mandatory = true,
                    TimeToLive = wait,
                    // parameters and the caller token must not reach the broker's persistent store
                    Persistent = false
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CommandNotRoutableException)
        {
            registry.MarkNoQueueBound(request.MethodId, sendStartedUtc);
            return NotAvailable(request, ReasonNoLiveHandler);
        }
        catch (DistributionTimeoutException)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: no answer within {Timeout} (outcome unknown)",
                request.MethodId, request.InvocationId, request.TenantId, wait);
            return Timeout(request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is DistributedOperationFailedException or OperationCanceledException)
        {
            // the bus is stopping or the send failed: a platform outcome, not an exception for the caller
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(Redactor(request).Redact(e),
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: bus transport failed (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, request.TenantId, correlationId);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>Masks the token and the sensitive parameters in exceptions of the bus or the resolver (AB#6914).</summary>
    private CkMethodRedactor Redactor(CkMethodRequest request) =>
        CkMethodRedactor.For(sensitivity.GetSensitiveParameters(request.TenantId, request.CkTypeId, request.MethodId),
            request);

    private CkMethodResponse NotAvailable(CkMethodRequest request, string reason)
    {
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: no handler available ({Reason})",
            request.MethodId, request.InvocationId, request.TenantId, reason);
        return CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.Reason, reason)));
    }

    private static CkMethodResponse Timeout(CkMethodRequest request) =>
        CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodTimeout,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.TimeoutSeconds,
                    CkMethodDeadline.ClampSeconds(request.TimeoutSeconds ?? CkMethodExecutionDto.DefaultTimeoutSeconds)
                        .ToString())));

    private static CkMethodResponse HandlerFailed(CkMethodRequest request, Guid correlationId) =>
        CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));
}
