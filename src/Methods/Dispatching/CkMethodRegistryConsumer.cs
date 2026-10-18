using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>Feeds the dispatcher's <see cref="CkMethodHandlerRegistry" /> from the handler broadcasts.</summary>
internal sealed class CkMethodRegistryConsumer(CkMethodHandlerRegistry registry, ILogger<CkMethodRegistryConsumer> logger)
    : IDistributedConsumer<CkMethodHandlerAnnounced>, IDistributedConsumer<CkMethodHandlerWithdrawn>
{
    public Task ConsumeAsync(IDistributedContext<CkMethodHandlerAnnounced> context)
    {
        var message = context.Message;
        logger.LogDebug("CK method handler {ServiceName}@{InstanceId} announced {MethodCount} method(s)",
            message.ServiceName, message.InstanceId, message.MethodIds.Count);
        registry.OnAnnounced(message);
        return Task.CompletedTask;
    }

    public Task ConsumeAsync(IDistributedContext<CkMethodHandlerWithdrawn> context)
    {
        var message = context.Message;
        logger.LogInformation("CK method handler {ServiceName}@{InstanceId} withdrew {MethodCount} method(s)",
            message.ServiceName, message.InstanceId, message.MethodIds.Count);
        registry.OnWithdrawn(message);
        return Task.CompletedTask;
    }
}
