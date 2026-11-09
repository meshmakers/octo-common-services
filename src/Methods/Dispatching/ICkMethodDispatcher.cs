using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     The one entry point to call a CK method from a gateway (GraphQL gateway, pipeline node, REST facade). It picks the
///     transport, the handler runs wherever it is hosted, and the caller always gets the same
///     <see cref="CkMethodResponse" /> envelope.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             <b>Resolution.</b> A method is routed by the element that <i>declares</i> it. When the request targets a
///             derived type, or a type that implements the interface that carries the method, the dispatcher rewrites
///             <see cref="CkMethodRequest.MethodId" /> to the qualified id of the declaring element (CK cache of the
///             tenant) before anything else runs, so filters, the queue name and the handler registration all see the
///             same id. Methods cannot be overridden (CK message 100), so there is exactly one declaring element.
///         </item>
///         <item>
///             <b>Transport.</b> A method with a handler in this process - registered with
///             <c>AddCkInProcessMethodHandlers</c>, local only (<c>HandleLocalOnly</c>), or as a bus handler of this
///             service (<c>AddCkMethodHandlers</c>, AB#8411) - runs in this process (transport <c>in-process</c>), nothing is
///             sent to the broker. Every other method is sent over the event hub to a shared, competing consumer endpoint
///             (transport <c>bus</c>). Both run through the same
///             <see cref="ICkMethodInvocationPipeline" />; the dispatcher adds no filter of its own.
///         </item>
///         <item>
///             <b>No handler.</b> When the handler registry knows no live handler for the method, the call fails as
///             <c>METHOD_HANDLER_NOT_AVAILABLE</c> (method id and the reason <c>no-live-handler</c>, no stack trace)
///             <b>without anything being sent to the broker</b> (AB#8634); right after the dispatcher started it first waits
///             for the registry warm-up (<see cref="CkMethodDispatcherOptions.RegistryWarmUp" />). The bus send itself
///             stays <i>mandatory</i>: when the registry still lists a handler that is gone (crash inside its TTL) the
///             broker returns the request and the call fails the same way within milliseconds.
///         </item>
///         <item>
///             <b>No retry.</b> The dispatcher sends a request once. A transport fault ends as <c>METHOD_TIMEOUT</c> or
///             <c>METHOD_HANDLER_NOT_AVAILABLE</c>, never as a second delivery.
///         </item>
///     </list>
/// </remarks>
public interface ICkMethodDispatcher
{
    /// <summary>
    ///     Calls the method. Never throws for a failing call (no handler, timeout, handler failure, filter failure): the
    ///     failure is the returned envelope. Only cancellation through <paramref name="cancellationToken" /> (the caller
    ///     gave up) surfaces as <see cref="OperationCanceledException" />.
    /// </summary>
    /// <param name="request">
    ///     The request. <see cref="CkMethodRequest.DeadlineUtc" /> is the absolute deadline of the call (now plus the
    ///     timeout of the method); the dispatcher waits for the answer until then plus
    ///     <see cref="CkMethodDispatcherOptions.ResponseGrace" />.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The handler's envelope unchanged, or a platform error envelope with an empty handler instance id.</returns>
    Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Options of the dispatcher (configuration section <c>Methods:Dispatcher</c>).</summary>
public sealed class CkMethodDispatcherOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Methods:Dispatcher";

    /// <summary>
    ///     Upper bound for the time a call may wait for the bus answer. A request with a later deadline is clamped to
    ///     it (also the deadline the handler sees). The CK compiler allows at most 300 s per method; the default of
    ///     10 minutes is a defensive limit only. Must be positive.
    /// </summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How long the dispatcher waits beyond the request deadline for the handler's own <c>METHOD_TIMEOUT</c> answer
    ///     before it answers <c>METHOD_TIMEOUT</c> itself. Documented tolerance of the timeout. Default 2 s.
    /// </summary>
    public TimeSpan ResponseGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Refuse a bus call with <c>METHOD_HANDLER_NOT_AVAILABLE</c> (reason <c>no-live-handler</c>) <b>without sending
    ///     anything to the broker</b> when the handler registry knows no live handler for the method (AB#8634). The request
    ///     carries the parameters (passwords of the identity methods) and the caller's access token; a mandatory send that
    ///     the broker returns has still carried them to the broker. Default <c>true</c>. <c>false</c> restores the advisory
    ///     registry of before (the broker decides with the mandatory flag) and is meant only as an emergency switch when
    ///     the registry feed is broken; it lets the parameters of unroutable calls reach the broker again.
    /// </summary>
    public bool RefuseWithoutLiveHandler { get; set; } = true;

    /// <summary>
    ///     Warm-up of the handler registry after the dispatcher started (and again after it asked for discovery): until it
    ///     ends, a call whose method has no live handler yet waits for an announcement (at most until the warm-up ends or
    ///     the request deadline) instead of being refused, so a freshly started gateway does not refuse calls a handler
    ///     will announce within one heartbeat round. Must be longer than the heartbeat interval of the handler hosts
    ///     (<c>Methods:HeartbeatInterval</c>, 15 s by default); zero or negative turns the wait off. Default 20 s.
    /// </summary>
    public TimeSpan RegistryWarmUp { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Keep the handler registry as a snapshot in the system database (AB#5671) so a restarted host answers without
    ///     waiting for the next heartbeat round. Takes effect only when the host registers the snapshot store
    ///     (<c>ICkMethodHandlerSnapshotStore</c>, part of the MongoDB runtime engine); without the store the registry is
    ///     in memory only, as before. Default <c>true</c>.
    /// </summary>
    public bool SnapshotEnabled { get; set; } = true;

    /// <summary>
    ///     How often changed registry rows are written to the snapshot store (a heartbeat round is coalesced into one
    ///     bulk write). Must be positive. Default 5 s.
    /// </summary>
    public TimeSpan SnapshotFlushInterval { get; set; } = TimeSpan.FromSeconds(5);
}
