using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Answers a dispatcher's <see cref="CkMethodHandlerDiscoveryRequested" /> with an immediate announcement.
/// </summary>
internal sealed class CkMethodHandlerDiscoveryConsumer(
    CkMethodHandlerInstance instance,
    IDistributionEventHubService eventHub,
    ILogger<CkMethodHandlerDiscoveryConsumer> logger) : IDistributedConsumer<CkMethodHandlerDiscoveryRequested>
{
    public async Task ConsumeAsync(IDistributedContext<CkMethodHandlerDiscoveryRequested> context)
    {
        // Not started yet: the startup service announces as soon as the endpoints are ready.
        if (!instance.Started.Task.IsCompletedSuccessfully || instance.Withdrawn)
        {
            return;
        }

        logger.LogDebug("CK method discovery requested by {RequestingInstanceId}; announcing {InstanceId}",
            context.Message.RequestingInstanceId, instance.InstanceId);
        await eventHub.PublishAsync(instance.CreateAnnouncement()).ConfigureAwait(false);
    }
}
