using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Handlers;

internal sealed class CkMethodHandlerInvoker(
    IServiceScopeFactory scopeFactory,
    CkMethodHandlerRegistrations registrations,
    CkMethodHandlerInstance instance,
    TimeProvider timeProvider,
    ILogger<CkMethodHandlerInvoker> logger) : ICkMethodHandlerInvoker
{
    // CancellationTokenSource rejects delays above int.MaxValue ms (~24.8 days)
    private static readonly TimeSpan MaxCancellationDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    public IReadOnlyCollection<string> MethodIds => instance.MethodIds;

    public bool CanHandle(string methodId) => registrations.Handlers.ContainsKey(methodId);

    public async Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CkMethodContract.IsSupported(request.ContractVersion))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: contract version {RequestedVersion} is newer than {SupportedVersion}",
                request.MethodId, request.InvocationId, request.TenantId, request.ContractVersion,
                CkMethodContract.CurrentVersion);
            return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
                CkMethodError.Create(CkMethodErrorCodes.MethodContractVersionUnsupported,
                    (CkMethodErrorParameters.RequestedVersion, request.ContractVersion.ToString()),
                    (CkMethodErrorParameters.SupportedVersion, CkMethodContract.CurrentVersion.ToString())));
        }

        if (!registrations.Handlers.TryGetValue(request.MethodId, out var registration))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: no handler registered in this service",
                request.MethodId, request.InvocationId, request.TenantId);
            return Failed(request, CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.Reason, "method-not-registered"));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (request.DeadlineUtc <= now)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} dropped: deadline {DeadlineUtc:O} already passed",
                request.MethodId, request.InvocationId, request.TenantId, request.DeadlineUtc);
            return TimeoutResponse(request, registration);
        }

        var correlationId = request.CorrelationId ?? request.InvocationId;
        var redactor = CkMethodRedactor.For(registration, request);
        var stopwatch = Stopwatch.StartNew();
        var remaining = request.DeadlineUtc - now;
        using var deadline = new CancellationTokenSource(
            remaining > MaxCancellationDelay ? MaxCancellationDelay : remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MethodId"] = request.MethodId,
            ["InvocationId"] = request.InvocationId,
            ["CorrelationId"] = correlationId,
            ["TenantId"] = request.TenantId
        });

        try
        {
            object parameters;
            try
            {
                parameters = registration.DeserializeParameters(request.ParametersJson);
            }
            catch (CkMethodParameterException e)
            {
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} rejected: invalid parameter '{Field}' ({Reason})",
                    request.MethodId, request.InvocationId, e.Field, e.Reason);
                return Failed(request, CkMethodErrorCodes.MethodValidationFailed,
                    (CkMethodErrorParameters.Field, e.Field), (CkMethodErrorParameters.Reason, e.Reason));
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);

            CkMethodAdapterOutcome outcome;
            try
            {
                outcome = await registration.Adapter
                    .InvokeAsync(handler, request, parameters, timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (CkMethodException e)
            {
                outcome = new CkMethodAdapterOutcome(e.Failure, null);
            }

            if (outcome.Failure is { } failure)
            {
                return DeclaredFailure(request, registration, failure, correlationId);
            }

            logger.LogDebug("CK method {MethodId} invocation {InvocationId} finished in {ElapsedMs} ms: success",
                request.MethodId, request.InvocationId, stopwatch.ElapsedMilliseconds);
            return CkMethodResponse.Succeeded(request.InvocationId, instance.InstanceId, outcome.ResultJson);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} cancelled at its deadline after {ElapsedMs} ms",
                request.MethodId, request.InvocationId, stopwatch.ElapsedMilliseconds);
            return TimeoutResponse(request, registration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The id in the log is the id the caller got in the error parameters. Secrets a handler put into its
            // exception message are masked; the stack trace never goes on the wire.
            logger.LogError(redactor.Redact(e),
                "CK method {MethodId} invocation {InvocationId} failed (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            return Failed(request, CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString()));
        }
    }

    private CkMethodResponse DeclaredFailure(CkMethodRequest request, CkMethodRegistration registration,
        CkMethodFailure failure, Guid correlationId)
    {
        // A handler may only answer with a code its method declares (or a platform code).
        if (registration.Definition?.Errors is { } declared &&
            !failure.Code.StartsWith(CkMethodErrorCodes.PlatformPrefix, StringComparison.Ordinal) &&
            declared.All(d => !string.Equals(d.Code, failure.Code, StringComparison.Ordinal)))
        {
            logger.LogError(
                "CK method {MethodId} invocation {InvocationId} failed (correlation {CorrelationId}): the handler returned error code {Code}, which the method does not declare",
                request.MethodId, request.InvocationId, correlationId, failure.Code);
            return Failed(request, CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString()));
        }

        logger.LogInformation("CK method {MethodId} invocation {InvocationId} finished with error {Code}",
            request.MethodId, request.InvocationId, failure.Code);
        return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId, WithMethodId(request, failure));
    }

    private static CkMethodError WithMethodId(CkMethodRequest request, CkMethodFailure failure)
    {
        var error = failure.ToWire();
        if (!failure.Code.StartsWith(CkMethodErrorCodes.PlatformPrefix, StringComparison.Ordinal) ||
            failure.Parameters.ContainsKey(CkMethodErrorParameters.MethodId))
        {
            return error;
        }

        // platform codes carry the method id (see the code table of the wire contract)
        var parameters = failure.Parameters.ToDictionary(p => p.Key, p => p.Value);
        parameters[CkMethodErrorParameters.MethodId] = request.MethodId;
        return error with { Parameters = parameters };
    }

    private CkMethodResponse TimeoutResponse(CkMethodRequest request, CkMethodRegistration registration)
    {
        int? timeout = registration.Definition is null
            ? null
            : registration.Definition.Execution?.TimeoutSeconds ?? CkMethodExecutionDto.DefaultTimeoutSeconds;
        return timeout is { } seconds
            ? Failed(request, CkMethodErrorCodes.MethodTimeout,
                (CkMethodErrorParameters.TimeoutSeconds, seconds.ToString()))
            : Failed(request, CkMethodErrorCodes.MethodTimeout);
    }

    private CkMethodResponse Failed(CkMethodRequest request, string code,
        params (string Name, string Value)[] parameters)
    {
        var all = new List<(string Name, string Value)> { (CkMethodErrorParameters.MethodId, request.MethodId) };
        all.AddRange(parameters);
        return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
            CkMethodError.Create(code, all.ToArray()));
    }
}
