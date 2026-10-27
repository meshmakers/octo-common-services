using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Handlers;

internal sealed class CkMethodHandlerInvoker(
    IServiceScopeFactory scopeFactory,
    CkMethodHandlerRegistrations registrations,
    CkMethodHandlerInstance instance,
    TimeProvider timeProvider,
    ILogger<CkMethodHandlerInvoker> logger,
    ICkMethodAuthorizationEvaluator authorizationEvaluator,
    ICkMethodEntryGuard? entryGuard = null,
    ICkMethodCallerTokenValidator? tokenValidator = null,
    IOptions<CkMethodLimitsOptions>? limits = null,
    IOptions<CkMethodDispatcherOptions>? dispatcherOptions = null) : ICkMethodHandlerInvoker
{
    // CancellationTokenSource rejects delays above int.MaxValue ms (~24.8 days)
    private static readonly TimeSpan MaxCancellationDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    public IReadOnlyCollection<string> MethodIds => instance.MethodIds;

    public bool CanHandle(string methodId) => registrations.Handlers.ContainsKey(methodId);

    /// <summary>
    ///     The remote (bus) path: when the host opted in to caller token validation, the forwarded token is validated
    ///     before the handler runs; the validated principal (none without validation) must then be allowed by the declared
    ///     authorization of the method.
    /// </summary>
    public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default) =>
        InvokeCoreAsync(request, null, cancellationToken);

    /// <summary>
    ///     The in-process path: the calling process authenticated the caller already, so no token is validated and the
    ///     principal it established is handed to the handler as is.
    /// </summary>
    internal Task<CkMethodResponse> InvokeInProcessAsync(CkMethodRequest request, CkMethodPrincipal? callerPrincipal,
        CancellationToken cancellationToken = default) =>
        InvokeCoreAsync(request, callerPrincipal, cancellationToken, validateToken: false);

    private async Task<CkMethodResponse> InvokeCoreAsync(CkMethodRequest request, CkMethodPrincipal? callerPrincipal,
        CancellationToken cancellationToken, bool validateToken = true)
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

        // The size is checked before the token is validated and long before the parameters are deserialized (AB#7665).
        if (CkMethodParameterLimit.Exceeds(request.ParametersJson, limits?.Value, out var actualBytes))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: parameters of {ActualBytes} bytes exceed the limit of {MaxBytes} bytes",
                request.MethodId, request.InvocationId, request.TenantId, actualBytes,
                (limits?.Value ?? new CkMethodLimitsOptions()).EffectiveMaxParametersBytes);
            return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
                CkMethodParameterLimit.TooLarge(request.MethodId, limits?.Value, actualBytes));
        }

        // A message published straight on the queue skips the dispatcher: the host applies the platform maximum itself
        // (the same helper and option as the dispatcher, AB#7665).
        var effectiveDeadline = request.DeadlineUtc;
        var maxTimeout = CkMethodDeadline.MaxOf(dispatcherOptions?.Value ?? new CkMethodDispatcherOptions());
        var clamped = CkMethodDeadline.Clamp(request.DeadlineUtc, now, maxTimeout);
        if (clamped != request.DeadlineUtc)
        {
            effectiveDeadline = clamped;
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: deadline {DeadlineUtc:O} is beyond the platform maximum of {MaxTimeout}, clamped",
                request.MethodId, request.InvocationId, request.TenantId, request.DeadlineUtc, maxTimeout);
        }

        var correlationId = request.CorrelationId ?? request.InvocationId;
        var redactor = CkMethodRedactor.For(registration, request);
        var stopwatch = Stopwatch.StartNew();
        var remaining = effectiveDeadline - now;
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
            if (validateToken && tokenValidator is not null)
            {
                // Before anything of the request is interpreted: an unauthenticated message learns nothing about the
                // parameters. The Caller member of the request is never consulted.
                var validation = await tokenValidator.ValidateAsync(request, linked.Token).ConfigureAwait(false);
                if (validation.Principal is not { } validated)
                {
                    logger.LogInformation(
                        "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: caller token refused ({Reason})",
                        request.MethodId, request.InvocationId, request.TenantId, validation.Reason);
                    return Failed(request, CkMethodErrorCodes.MethodForbidden,
                        (CkMethodErrorParameters.Reason, validation.Reason ?? CkMethodTokenReasons.TokenInvalid));
                }

                callerPrincipal = validated;
            }

            // The declared authorization (roles, scopes, allowSelf, default-deny) is enforced here on every path that
            // reaches a handler, with the one evaluator the gateway uses (AB#7664). A registration without a definition
            // cannot be verified and is refused. The refusal is the envelope of the gateway: it does not say which rule
            // failed and the target is never read.
            var definition = registration.Definition;
            var decision = definition is null
                ? CkMethodAuthorizationDecision.Deny(CkMethodDenial.UnknownMethod)
                : authorizationEvaluator.Evaluate(definition.Authorization, definition.Kind, request.TargetRtId,
                    callerPrincipal);
            if (!decision.Allowed)
            {
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: caller refused by the declared authorization ({Denial})",
                    request.MethodId, request.InvocationId, request.TenantId, decision.Denial);
                return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
                    CkMethodAuthorizationErrors.Forbidden(request.MethodId));
            }

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
            if (entryGuard?.FindViolation(scope.ServiceProvider) is { } violation)
            {
                // Tripwire (OD-8): request-bound ambient state is visible at handler entry. The handler is not created.
                // The log names the probe, never the state it looked at.
                logger.LogError(
                    "CK method {MethodId} invocation {InvocationId} NOT executed: ambient state probe {ProbeType} {Verdict} at handler entry (correlation {CorrelationId})",
                    request.MethodId, request.InvocationId, violation.ProbeType.FullName,
                    violation.Fault is null ? "reports request-bound state" : "failed with " + violation.Fault.GetType().FullName,
                    correlationId);
                return Failed(request, CkMethodErrorCodes.MethodHandlerFailed,
                    (CkMethodErrorParameters.CorrelationId, correlationId.ToString()));
            }

            var handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);

            CkMethodAdapterOutcome outcome;
            try
            {
                outcome = await registration.Adapter
                    .InvokeAsync(handler, request, parameters, timeProvider, callerPrincipal, linked.Token).ConfigureAwait(false);
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
            logger.LogError(LoggedException(registration, redactor, e),
                "CK method {MethodId} invocation {InvocationId} failed (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            return Failed(request, CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString()));
        }
    }

    /// <summary>
    ///     The exception as it may be logged. The fault of a result serialization quotes the result value more often than
    ///     not; of a <b>sensitive</b> result only the type of the fault is logged (AB#7007), never its message or stack.
    ///     Everything else is masked as before.
    /// </summary>
    private static Exception LoggedException(CkMethodRegistration registration, CkMethodRedactor redactor, Exception e) =>
        e is CkMethodResultSerializationException serialization
            ? registration.SensitiveResult
                ? new InvalidOperationException(
                    "The method result could not be serialized (" + serialization.InnerException?.GetType().FullName +
                    "); details are withheld because the result is sensitive.")
                : redactor.Redact(serialization.InnerException ?? serialization)
            : redactor.Redact(e);

    /// <summary>
    ///     <c>true</c> when the result of the method is sensitive; fail-closed for a method that is not registered here.
    /// </summary>
    internal bool IsResultSensitive(string methodId) =>
        !registrations.Handlers.TryGetValue(methodId, out var registration) || registration.SensitiveResult;

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

    /// <summary>
    ///     The <c>METHOD_TIMEOUT</c> answer of this invoker for a request, used by transports that enforce the deadline
    ///     themselves (a handler that ignores its cancellation token). Same envelope as the invoker's own timeout.
    /// </summary>
    internal CkMethodResponse TimeoutFor(CkMethodRequest request) =>
        registrations.Handlers.TryGetValue(request.MethodId, out var registration)
            ? TimeoutResponse(request, registration)
            : Failed(request, CkMethodErrorCodes.MethodTimeout);

    private CkMethodResponse TimeoutResponse(CkMethodRequest request, CkMethodRegistration registration)
    {
        // The request carries the timeout its deadline was derived from; the definition is the fallback for a sender that
        // does not know it, and without either the parameter is left out rather than guessed.
        int? timeout = request.TimeoutSeconds is { } requested
            ? requested
            : registration.Definition is null
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
