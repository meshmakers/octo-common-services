using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Metrics;
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
        var started = timeProvider.GetTimestamp();
        // Which transport the call is on, and whether it entered the pipeline: the pipeline counts every call it runs
        // (also one whose exception escapes it) and the band-100 audit filter records the calls that reach the filters.
        // Only what happens before that is the dispatcher's to count and record (AB#7930).
        var transport = CkMethodTransportNames.Bus;
        var pipelineEntered = false;
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
                transport = CkMethodTransportNames.InProcess;
                pipelineEntered = true;
                return await inProcess.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }

            pipelineEntered = true;
            return await pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request,
                r => SendAsync(r, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up. Inside the pipeline the audit filter records it; a cancellation that surfaces before the
            // pipeline (a resolver that observes the token) has no other observer than this line.
            if (!pipelineEntered)
            {
                await AuditAsync(request, transport, CkMethodAuditOutcomes.CallerCancelled, started, countCall: true,
                    cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception e)
        {
            // The contract is "never throws for a failing call"; the pipeline and the transports already answer their own
            // failures, this is the last line for the rest (resolver, DI, and what escapes the pipeline before its filters).
            var correlationId = request.CorrelationId ?? request.InvocationId;
            logger.LogError(SafeRedacted(request, e), "CK method {MethodId} invocation {InvocationId} failed in the dispatcher (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            // AB#7930: the answer is a response, so the gateway (which records only exceptions it catches) does not see a
            // fault. An exception that escapes the pipeline was counted by the pipeline but no filter ran, so it needs the
            // event only; a fault before the pipeline needs the event and the count.
            await AuditAsync(request, transport, CkMethodErrorCodes.MethodHandlerFailed, started, countCall: !pipelineEntered,
                cancellationToken).ConfigureAwait(false);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>
    ///     Records the one audit event of a call that ended in the dispatcher before the filters of the pipeline could see it
    ///     (and counts it when the pipeline did not). Never throws: a failing recorder must not change the answer, and the
    ///     event is written with a token that is not cancelled, like the audit filter does. Without
    ///     <c>AddCkMethodAudit</c> there is no recorder and nothing is recorded.
    /// </summary>
    private async Task AuditAsync(CkMethodRequest request, string transport, string outcome, long started, bool countCall,
        CancellationToken cancellationToken)
    {
        var duration = timeProvider.GetElapsedTime(started);
        if (countCall)
        {
            CkMethodMetrics.RecordCall(request.MethodId, transport, outcome, duration);
        }

        try
        {
            if (serviceProvider.GetService(typeof(ICkMethodAuditRecorder)) is not ICkMethodAuditRecorder recorder)
            {
                return;
            }

            var subject = TrustedSubject(request, transport, cancellationToken);
            await recorder.RecordAsync(CkMethodAuditRecord.For(request, outcome, transport, duration, subject),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (CkMethodAuditFailedException)
        {
            // logged and counted by the recorder; the call is answered as failed anyway
        }
        catch (Exception e)
        {
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit event of the dispatcher could not be recorded",
                request.MethodId, request.InvocationId);
        }
    }

    /// <summary>The trusted caller like the audit filter reads it; <c>null</c> falls back to <c>request.Caller</c>.</summary>
    private string? TrustedSubject(CkMethodRequest request, string transport, CancellationToken cancellationToken)
    {
        try
        {
            return (serviceProvider.GetService(typeof(ICkMethodPrincipalProvider)) as ICkMethodPrincipalProvider)
                ?.GetPrincipal(new CkMethodInvocationContext(transport, request, cancellationToken))?.Subject;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "CK method {MethodId}: the caller for the audit event could not be determined",
                request.MethodId);
            return null;
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

    /// <summary>
    ///     <see cref="Redactor" /> for the last line: a fault of the sensitivity lookup itself must not escape the dispatcher
    ///     (nothing of the exception is logged then, it may quote the payload).
    /// </summary>
    private Exception? SafeRedacted(CkMethodRequest request, Exception e)
    {
        try
        {
            return Redactor(request).Redact(e);
        }
        catch
        {
            return null;
        }
    }

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
