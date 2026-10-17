using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Re-announces the handler instance every <see cref="CkMethodHandlerOptions.HeartbeatInterval" /> and, on
///     graceful shutdown, publishes <c>CkMethodHandlerWithdrawn</c> before removing the method endpoints. The bus is
///     still running at that point: common-services never stops it (contract C12).
/// </summary>
internal sealed class CkMethodHandlerHeartbeatService(
    CkMethodHandlerInstance instance,
    IDistributionEventHubService eventHub,
    IOptions<CkMethodHandlerOptions> options,
    TimeProvider timeProvider,
    ILogger<CkMethodHandlerHeartbeatService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // host shutdown timeout
            }
        }

        if (!instance.Started.Task.IsCompletedSuccessfully)
        {
            return;
        }

        instance.MarkWithdrawn();
        try
        {
            await eventHub.PublishAsync(instance.CreateWithdrawal(), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("CK method handler {ServiceName}@{InstanceId} withdrawn", instance.ServiceName,
                instance.InstanceId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Publishing the CK method handler withdrawal failed");
        }

        foreach (var handle in instance.TakeEndpointHandles())
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Removing a CK method endpoint failed");
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await instance.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // startup failed; it is logged by the initializer
            return;
        }

        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (instance.Withdrawn)
                {
                    return;
                }

                try
                {
                    await eventHub.PublishAsync(instance.CreateAnnouncement(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "CK method handler heartbeat failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
