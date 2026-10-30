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
///             <b>Transport.</b> A method registered with <c>AddCkInProcessMethodHandlers</c> runs in this process
///             (transport <c>in-process</c>). Every other method is sent over the event hub to a shared, competing
///             consumer endpoint (transport <c>bus</c>). Both run through the same
///             <see cref="ICkMethodInvocationPipeline" />; the dispatcher adds no filter of its own.
///         </item>
///         <item>
///             <b>No handler.</b> The bus send is <i>mandatory</i>: when nobody consumes the queue the call fails within
///             milliseconds as <c>METHOD_HANDLER_NOT_AVAILABLE</c> (method id and a reason token, no stack trace).
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
