using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Runs a registered method handler for a wire request and returns the response envelope. This is the transport
///     independent core of the handler host: the bus consumer of this package and any other transport (in-process, tests)
///     call it, so all of them behave the same.
///     It never throws for a failing request or handler: every outcome, including contract version mismatch, deadline,
///     invalid parameters and handler exceptions, is a <see cref="CkMethodResponse" />. Only cancellation through the
///     <c>cancellationToken</c> argument (the caller gave up) surfaces as <see cref="OperationCanceledException" />.
/// </summary>
public interface ICkMethodHandlerInvoker
{
    /// <summary>The ids of the methods this service has handlers for.</summary>
    IReadOnlyCollection<string> MethodIds { get; }

    /// <summary>True when a handler is registered for the method id.</summary>
    bool CanHandle(string methodId);

    /// <summary>
    ///     Runs the handler. The handler's cancellation token fires at the request's <c>DeadlineUtc</c> (or when
    ///     <paramref name="cancellationToken" /> fires).
    /// </summary>
    Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default);
}
