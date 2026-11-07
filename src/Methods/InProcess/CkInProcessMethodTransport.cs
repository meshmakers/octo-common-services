using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Runs the CK methods that the calling service owns itself, without the event hub. The handler is the same typed
///     handler class as for a remote method (<see cref="ICkMethodHandler{TParameters}" />), the request and the response
///     are the wire contract types, and the answer is the same envelope the bus path produces: contract version check,
///     deadline, parameter and result JSON (<see cref="CkMethodJson.Options" />), language-neutral errors and redaction
///     come from the one <see cref="ICkMethodHandlerInvoker" /> core.
///     A handler registered in-process is never published to the bus and never announced as a remote handler; a dispatcher
///     asks <see cref="CanHandle" /> before its registry lookup.
///     The transport also runs the handlers that this service hosts <b>on the bus</b> (<c>AddCkMethodHandlers</c>) when the
///     service calls them itself (AB#8411, e.g. from a REST facade): same invoker core, same authorization evaluator, same
///     handler instance id as the bus consumer, so the envelope is the one a remote caller would get - only the bus hop is
///     left out and no parameter reaches the broker.
///     The handler runs in a <b>new DI scope</b> of the root provider, not in the scope of the caller's HTTP request, and
///     outside the caller's database transaction. It also runs <b>detached from the caller's ambient execution context</b>
///     (OD-8): an <c>AsyncLocal</c> the caller set (the request session, a principal scope) is empty inside the handler.
///     Only the trace (the caller's activity is the parent of the handler span), the culture and the identifying logging
///     scope (method id, invocation id, correlation id, tenant) are carried across, and the cancellation token with the
///     deadline. Registered <see cref="ICkInProcessAmbientStateProbe" />s are asked at handler entry as a tripwire.
/// </summary>
public interface ICkInProcessMethodTransport
{
    /// <summary>
    ///     The ids of the methods this transport runs: the in-process (and local-only) registrations and the bus handlers
    ///     hosted by this service.
    /// </summary>
    IReadOnlyCollection<string> MethodIds { get; }

    /// <summary>
    ///     True when the method has a handler in this process: registered in-process, local only, or as a bus handler of
    ///     this service.
    /// </summary>
    bool CanHandle(string methodId);

    /// <summary>
    ///     Runs the registered filters (<see cref="ICkMethodInvocationFilter" />) and the handler. Never throws for a
    ///     failing request or handler; only cancellation through <paramref name="cancellationToken" /> (the caller gave
    ///     up) surfaces as <see cref="OperationCanceledException" />. A handler that ignores its token is not waited for
    ///     past the request's <c>DeadlineUtc</c>: the caller gets <c>METHOD_TIMEOUT</c> exactly as on the bus.
    ///     A method that is not registered in-process answers <c>METHOD_HANDLER_NOT_AVAILABLE</c> immediately.
    /// </summary>
    Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The in-process handler registrations (kept apart from the remote ones, which are announced).</summary>
internal sealed class CkInProcessMethodRegistrations : CkMethodHandlerRegistrations;

internal sealed class CkInProcessMethodTransport : ICkInProcessMethodTransport
{
    // CancellationTokenSource / Task.WaitAsync reject delays above int.MaxValue ms
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private readonly CkMethodHandlerInvoker _invoker;
    private readonly CkMethodHandlerInvoker? _hostedInvoker;
    private readonly ICkMethodInvocationPipeline _pipeline;
    private readonly CkInProcessMethodRegistrations _registrations;
    private readonly TimeProvider _timeProvider;
    private readonly ICkMethodPrincipalProvider? _principalProvider;
    private readonly IOptions<CkMethodDispatcherOptions>? _dispatcherOptions;

    public CkInProcessMethodTransport(
        IServiceScopeFactory scopeFactory,
        CkInProcessMethodRegistrations registrations,
        ICkMethodInvocationPipeline pipeline,
        IOptions<CkMethodHandlerOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IEnumerable<ICkInProcessAmbientStateProbe>? probes = null,
        ICkMethodPrincipalProvider? principalProvider = null,
        ICkMethodAuthorizationEvaluator? authorizationEvaluator = null,
        IOptions<CkMethodLimitsOptions>? limits = null,
        IOptions<CkMethodDispatcherOptions>? dispatcherOptions = null,
        CkMethodHandlerRegistrations? hostedRegistrations = null,
        CkMethodHandlerInstance? hostedInstance = null)
    {
        _registrations = registrations;
        _pipeline = pipeline;
        _principalProvider = principalProvider;
        _dispatcherOptions = dispatcherOptions;
        _timeProvider = timeProvider;
        // The instance id of the envelope is the id of this in-process host; it is never announced.
        var instance = new CkMethodHandlerInstance(registrations, options, timeProvider);
        var evaluator = authorizationEvaluator ?? throw new InvalidOperationException(
            "ICkMethodAuthorizationEvaluator is not registered; call AddCkMethodAuthorization().");
        var entryGuard = new CkInProcessEntryGuard(probes ?? []);
        _invoker = new CkMethodHandlerInvoker(scopeFactory, registrations, instance, timeProvider,
            loggerFactory.CreateLogger<CkMethodHandlerInvoker>(), evaluator, entryGuard, limits: limits,
            dispatcherOptions: dispatcherOptions);
        if (hostedRegistrations is { Handlers.Count: > 0 })
        {
            // AB#8411: the bus handlers of this service, called by the service itself. The registrations are the ones the
            // bus consumer uses (validated definitions, sensitive parameters); the instance is the announced one, so the
            // envelope carries the same handler instance id as over the bus. No token is validated: the principal is the
            // one the calling code established (CkMethodPrincipalScope), exactly as for an in-process registration.
            _hostedInvoker = new CkMethodHandlerInvoker(scopeFactory, hostedRegistrations,
                hostedInstance ?? new CkMethodHandlerInstance(hostedRegistrations, options, timeProvider), timeProvider,
                loggerFactory.CreateLogger<CkMethodHandlerInvoker>(), evaluator, entryGuard, limits: limits,
                dispatcherOptions: dispatcherOptions);
            MethodIds = _invoker.MethodIds.Concat(_hostedInvoker.MethodIds).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
        }
        else
        {
            MethodIds = _invoker.MethodIds;
        }
    }

    public IReadOnlyCollection<string> MethodIds { get; }

    public bool CanHandle(string methodId) => _invoker.CanHandle(methodId) || _hostedInvoker?.CanHandle(methodId) == true;

    /// <summary>The invoker of the method: the in-process registrations first, then the hosted bus handlers.</summary>
    private CkMethodHandlerInvoker InvokerFor(string methodId) =>
        _hostedInvoker != null && !_invoker.CanHandle(methodId) && _hostedInvoker.CanHandle(methodId)
            ? _hostedInvoker
            : _invoker;

    public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, request,
            r => RunAsync(r, cancellationToken), cancellationToken);
    }

    private async Task<CkMethodResponse> RunAsync(CkMethodRequest request, CancellationToken cancellationToken)
    {
        // The platform maximum applies to the wait as well (AB#7665): a handler that ignores its token is answered
        // METHOD_TIMEOUT at the clamped deadline, and the handler context shows the clamped deadline.
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var clamped = CkMethodDeadline.Clamp(request.DeadlineUtc, now,
            CkMethodDeadline.MaxOf(_dispatcherOptions?.Value ?? new CkMethodDispatcherOptions()));
        if (clamped != request.DeadlineUtc)
        {
            request = request with { DeadlineUtc = clamped };
        }

        var remaining = request.DeadlineUtc - now;
        // The handler runs detached from the caller's ExecutionContext (OD-8): see CkDetachedExecution.
        // The principal is read here, in the caller's flow, before the handler is detached from it: the calling process
        // (the gateway) authenticated the caller, so nothing is re-validated and the handler gets that principal.
        var principal = (_principalProvider ?? CkMethodPrincipalScope.Instance)
            .GetPrincipal(new CkMethodInvocationContext(CkMethodTransportNames.InProcess, request, cancellationToken));
        var invoker = InvokerFor(request.MethodId);
        var run = CkDetachedExecution.Run(request.MethodId, CkMethodTransportNames.InProcess,
            () => invoker.InvokeInProcessAsync(request, principal, cancellationToken));
        if (remaining <= TimeSpan.Zero || run.IsCompleted)
        {
            return await run.ConfigureAwait(false);
        }

        try
        {
            return await run.WaitAsync(remaining > MaxWait ? MaxWait : remaining, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The handler ignored its token and is still running. The caller has to get its answer at the deadline,
            // like on the bus; the late result is dropped.
            _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return invoker.TimeoutFor(request);
        }
    }

    internal CkInProcessMethodRegistrations Registrations => _registrations;
}
