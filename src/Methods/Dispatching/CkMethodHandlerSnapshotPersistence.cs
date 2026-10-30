using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>
///     Connects the in-memory <see cref="CkMethodHandlerRegistry" /> to the optional
///     <see cref="ICkMethodHandlerSnapshotStore" /> (AB#5671): loads the snapshot at startup and writes changed rows in
///     the background. A store failure never affects a call: the registry stays advisory and in memory.
/// </summary>
/// <remarks>
///     The registry is only touched when a store is registered and <see cref="CkMethodDispatcherOptions.SnapshotEnabled" />
///     is set; otherwise nothing is queued and the behavior is exactly the in-memory one.
/// </remarks>
internal sealed class CkMethodHandlerSnapshotPersistence(
    CkMethodHandlerRegistry registry,
    IOptions<CkMethodDispatcherOptions> options,
    ILogger<CkMethodHandlerSnapshotPersistence> logger,
    ICkMethodHandlerSnapshotStore? store = null)
{
    internal bool IsEnabled => store != null && options.Value.SnapshotEnabled;

    internal TimeSpan FlushInterval =>
        options.Value.SnapshotFlushInterval > TimeSpan.Zero ? options.Value.SnapshotFlushInterval : TimeSpan.FromSeconds(5);

    /// <summary>Loads the snapshot into the registry and starts queueing changes. Never throws.</summary>
    internal async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Track first: an announcement that arrives while the snapshot loads is written back, and it wins over the
        // loaded row anyway (Restore keeps the newer in-memory entry).
        registry.EnablePersistence();
        try
        {
            var rows = await store!.LoadAsync(cancellationToken).ConfigureAwait(false);
            var restored = registry.Restore(rows);
            logger.LogInformation("CK method handler registry snapshot loaded: {Restored} of {Rows} row(s) taken over",
                restored, rows.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Loading the CK method handler registry snapshot failed; the registry fills from heartbeats");
        }
    }

    /// <summary>Writes the rows changed since the last flush. A failed or deferred write is retried by the next flush.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var rows = registry.TakeChanges();
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            if (!await store!.UpsertAsync(rows, cancellationToken).ConfigureAwait(false))
            {
                registry.Requeue(rows); // system tenant not there yet
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            registry.Requeue(rows);
            logger.LogWarning(e, "Writing the CK method handler registry snapshot failed; retrying with the next flush");
        }
    }
}

/// <summary>
///     Loads the registry snapshot at startup. Order 28: after the event hub (20) and the in-process endpoints (25), before
///     the discovery request (30), so the restored rows and the answers to the discovery merge in the right order.
/// </summary>
internal sealed class CkMethodHandlerSnapshotLoadService(CkMethodHandlerSnapshotPersistence persistence)
    : IAsyncInitializationService
{
    public int Order => 28;

    public Task InitializeAsync() => persistence.LoadAsync();
}

/// <summary>Writes changed registry rows every <see cref="CkMethodDispatcherOptions.SnapshotFlushInterval" /> and once more at shutdown.</summary>
internal sealed class CkMethodHandlerSnapshotFlushService(
    CkMethodHandlerSnapshotPersistence persistence,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!persistence.IsEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(persistence.FlushInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await persistence.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await persistence.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
