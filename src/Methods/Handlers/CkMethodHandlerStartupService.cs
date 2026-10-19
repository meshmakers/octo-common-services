using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Connects one shared command endpoint per registered method, waits until every endpoint accepts messages
///     (<see cref="EndpointHandle.WaitUntilReadyAsync" />) and only then announces the instance, so a dispatcher never
///     routes a request to a queue that does not exist yet (AB#6891).
///     Order 25: after <c>EventHubStartupService</c> (20) has started the bus.
/// </summary>
internal sealed class CkMethodHandlerStartupService(
    IEventHubControl eventHubControl,
    IDistributionEventHubService eventHub,
    CkMethodHandlerInstance instance,
    CkMethodBusConsumer consumer,
    IOptions<CkMethodHandlerOptions> options,
    ILogger<CkMethodHandlerStartupService> logger) : IAsyncInitializationService
{
    // Method queues carry passwords and tokens: no retry, no Fault<T> with the request, no _error/_skipped copies.
    // ConfigureConsumeTopology stays off, a publish of CkMethodRequest must not fan out to the method queues.
    private static readonly CommandConsumerOptions EndpointOptions = new() { SuppressFaults = true };

    public int Order => 25;

    public async Task InitializeAsync()
    {
        if (instance.Started.Task.IsCompleted)
        {
            return;
        }

        try
        {
            var endpoints = new List<(string MethodId, string QueueName, EndpointHandle Handle)>();
            foreach (var methodId in instance.MethodIds)
            {
                var queueName = CkMethodQueueNames.For(methodId);
                var handle = eventHubControl.RegisterCommandConsumer(queueName, consumer.CreateHandler(),
                    EndpointOptions);
                instance.AddEndpointHandle(handle);
                endpoints.Add((methodId, queueName, handle));
            }

            await WaitUntilReadyAsync(endpoints).ConfigureAwait(false);

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

    private async Task WaitUntilReadyAsync(List<(string MethodId, string QueueName, EndpointHandle Handle)> endpoints)
    {
        using var timeout = new CancellationTokenSource(options.Value.EndpointReadyTimeout);
        foreach (var (methodId, queueName, handle) in endpoints)
        {
            try
            {
                await handle.WaitUntilReadyAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"CK method {methodId}: endpoint {queueName} did not accept messages within " +
                    $"{options.Value.EndpointReadyTimeout}; the handler is not announced. Typical causes: the broker is not " +
                    "reachable, or a queue of this name already exists with different arguments (PRECONDITION_FAILED, " +
                    "e.g. a replica of an older build during a rolling update); the bus log names the reason. " +
                    "The existing queue is left untouched.");
            }

            logger.LogInformation("CK method {MethodId} handler endpoint {QueueName} ready", methodId, queueName);
        }
    }
}
