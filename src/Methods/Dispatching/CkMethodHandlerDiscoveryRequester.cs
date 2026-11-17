using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     Asks every handler instance to announce itself again after the broker returned a send of a method whose handler
///     the registry still listed (no queue bound, for example while a handler re-binds its queue after a broker
///     reconnect). Live handlers answer within milliseconds instead of with their next heartbeat (up to one heartbeat
///     interval). Fire and forget: <see cref="Request" /> never blocks or fails a call. At most one request is in flight
///     and at most one is published per <see cref="MinInterval" />, so a burst of returned sends or waiting calls
///     publishes one broadcast, not one per call.
/// </summary>
internal sealed class CkMethodHandlerDiscoveryRequester(
    IDistributionEventHubService eventHub,
    TimeProvider timeProvider,
    ILogger<CkMethodHandlerDiscoveryRequester> logger)
{
    /// <summary>Minimum time between two discovery requests of this dispatcher.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     A publish that takes longer is abandoned (it may still complete), so a publish that waits for a lost broker
    ///     connection cannot block every later request.
    /// </summary>
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The discovery request of this dispatcher instance (also published at start).</summary>
    internal static CkMethodHandlerDiscoveryRequested CreateMessage(TimeProvider timeProvider) =>
        new(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime)
        {
            RequestingInstanceId = CkMethodDispatcherStartupService.DispatcherInstanceId
        };

    private readonly Lock _lock = new();
    private bool _inFlight;
    private long _lastStartedTimestamp;
    private bool _started;

    /// <summary>Number of discovery requests this instance published (tests and diagnostics).</summary>
    internal int Published { get; private set; }

    /// <summary>
    ///     Publishes a discovery request in the background unless one is in flight or the last one started less than
    ///     <see cref="MinInterval" /> ago. Returns <c>true</c> when a request was started.
    /// </summary>
    internal bool Request()
    {
        var now = timeProvider.GetTimestamp();
        lock (_lock)
        {
            if (_inFlight || (_started && timeProvider.GetElapsedTime(_lastStartedTimestamp, now) < MinInterval))
            {
                return false;
            }

            _inFlight = true;
            _started = true;
            _lastStartedTimestamp = now;
        }

        // Task.Run: a publish that waits for the broker connection must not hold the caller's thread
        _ = Task.Run(PublishAsync);
        return true;
    }

    private async Task PublishAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(PublishTimeout, timeProvider);
            await eventHub.PublishAsync(CreateMessage(timeProvider), timeout.Token)
                .WaitAsync(PublishTimeout, timeProvider).ConfigureAwait(false);
            lock (_lock)
            {
                Published++;
            }
        }
        catch (Exception e)
        {
            // Not fatal: the handlers announce themselves with their next heartbeat anyway. The type only: nothing of a
            // call is in this message, but keep the log line free of anything a transport may quote.
            logger.LogWarning("Publishing the CK method handler discovery request failed ({ExceptionType})", e.GetType().Name);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = false;
            }
        }
    }
}
