using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     Asks every handler instance to announce itself right after the bus has started, so the registry fills within
///     milliseconds instead of after up to one heartbeat interval. Order 30: after the event hub (20) and the endpoints
///     of handlers hosted in the same process (25).
/// </summary>
internal sealed class CkMethodDispatcherStartupService(
    IDistributionEventHubService eventHub,
    TimeProvider timeProvider,
    ILogger<CkMethodDispatcherStartupService> logger) : IAsyncInitializationService
{
    internal static readonly string DispatcherInstanceId = Guid.NewGuid().ToString("N");

    public int Order => 30;

    public async Task InitializeAsync()
    {
        try
        {
            await eventHub.PublishAsync(new CkMethodHandlerDiscoveryRequested(Guid.NewGuid(),
                timeProvider.GetUtcNow().UtcDateTime) { RequestingInstanceId = DispatcherInstanceId }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Not fatal: the registry fills with the next heartbeat (15 s) and the send does not depend on it.
            logger.LogWarning(e, "Publishing the CK method handler discovery request failed");
        }
    }
}
