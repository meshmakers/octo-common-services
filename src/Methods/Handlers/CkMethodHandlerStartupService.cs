using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Connects one shared command endpoint per registered method and announces the instance.
///     Order 25: after <c>EventHubStartupService</c> (20) has started the bus.
/// </summary>
internal sealed class CkMethodHandlerStartupService(
    IEventHubControl eventHubControl,
    IDistributionEventHubService eventHub,
    CkMethodHandlerInstance instance,
    CkMethodBusConsumer consumer,
    ILogger<CkMethodHandlerStartupService> logger) : IAsyncInitializationService
{
    public int Order => 25;

    public async Task InitializeAsync()
    {
        if (instance.Started.Task.IsCompleted)
        {
            return;
        }

        try
        {
            foreach (var methodId in instance.MethodIds)
            {
                var queueName = CkMethodQueueNames.For(methodId);
                var handle = eventHubControl.RegisterCommandConsumer(queueName, consumer.CreateHandler());
                instance.AddEndpointHandle(handle);
                // TODO(event hub H3a): wait here until the endpoint is routable (EndpointHandle.WaitUntilReadyAsync) before
                // announcing. The current event hub does not expose readiness; see CLAUDE.md "Known limitations".
                logger.LogInformation("CK method {MethodId} handler endpoint {QueueName} connected", methodId, queueName);
            }

            await eventHub.PublishAsync(instance.CreateAnnouncement()).ConfigureAwait(false);
            logger.LogInformation("CK method handler {ServiceName}@{InstanceId} announced {MethodCount} method(s)",
                instance.ServiceName, instance.InstanceId, instance.MethodIds.Count);
            instance.Started.TrySetResult();
        }
        catch (Exception e)
        {
            instance.Started.TrySetException(e);
            throw;
        }
    }
}
