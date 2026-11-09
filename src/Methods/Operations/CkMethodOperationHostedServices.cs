using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     Keeps the heartbeat of the operations this host works on (every <c>HeartbeatInterval</c>) and stops them on a
///     graceful shutdown (they end as lost, <c>METHOD_OUTCOME_UNKNOWN</c>).
/// </summary>
internal sealed class CkMethodOperationHeartbeatService(
    CkMethodOperationRunner runner,
    IOptions<CkMethodOperationOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await runner.HeartbeatAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // first stop the work (heartbeats keep running meanwhile), then the timer
        await runner.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     Ends lost operations of every host (no heartbeat for <c>StaleAfter</c>) every <c>SweepInterval</c>. Any number of
///     replicas may run it: the store ends a record only while it is open.
/// </summary>
internal sealed class CkMethodOperationSweepService(
    CkMethodOperationRunner runner,
    IOptions<CkMethodOperationOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await runner.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
