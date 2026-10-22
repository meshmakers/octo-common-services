using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Periodic loop that retries tenant setup for two failure sources:
///     <list type="bullet">
///         <item>
///             Tenants whose deferred <see cref="IDefaultConfigurationCreatorService.StartDeferredTenantsAsync"/>
///             call failed (tracked in <see cref="FailedTenantRegistry"/>).
///         </item>
///         <item>
///             Tenants whose setup failed (in-memory <see cref="TenantSetupRetryTracker"/>, optional durable store)
///             or whose identity-data setup was deferred or rejected with a transient condition during
///             <c>SetupTenantAsync</c> (the standardized creator's internal pending list).
///         </item>
///     </list>
/// </summary>
/// <remarks>
///     <para>
///         AB#4208 — the loop is not gated on <c>failedTenantRegistry.HasFailedTenants</c>:
///         <c>RetryFailedTenantsAsync</c> is itself self-gating (returns immediately when all queues are
///         empty), so there is no measurable cost, and a service whose <c>StartTenantAsync</c> is a no-op
///         never populates the registry but may still have pending identity data.
///     </para>
///     <para>
///         AB#7271 — this used to be a hosted <c>BackgroundService</c>. Hosted services start one after the
///         other, and <see cref="HostedInitializer"/> blocks its <c>StartAsync</c> for the whole startup pass
///         (all tenants), so the loop only started once the slowest tenant was done: a retry announced at
///         "+30 s" waited minutes. <see cref="HostedInitializer"/> now starts this loop <b>before</b> the pass
///         and stops it with the host. The first tick is one <see cref="RetryInterval"/> after the start, the
///         following ones every <see cref="RetryInterval"/>, so a due retry waits at most for the next tick. A tick
///         runs the retries one after the other and the next tick only starts when it has finished, so a slow
///         tenant delays the others; that serial execution is unchanged. Time comes from
///         <see cref="TimeProvider"/> (fake time in tests).
///     </para>
/// </remarks>
internal sealed class FailedTenantRetryLoop(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<FailedTenantRetryLoop> logger,
    // Resolved here so the tenant-setup metrics exist from startup and the tracker is configured before
    // its first use (AB#7249). Not used otherwise.
    TenantSetupMetrics tenantSetupMetrics,
    TimeProvider? timeProvider = null)
{
    /// <summary>Tick of the loop: the cadence at which due retries are picked up.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly TenantSetupMetrics _tenantSetupMetrics = tenantSetupMetrics;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Starts the loop (idempotent). Returns immediately.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // Created here, not inside the task: the first tick is measured from Start(), however late the
            // thread pool schedules the loop.
            var timer = new PeriodicTimer(RetryInterval, _timeProvider);
            _loop = Task.Run(() => RunAsync(timer, token), CancellationToken.None);
        }
    }

    /// <summary>
    ///     Stops the loop and waits for the running tick to finish, but not longer than
    ///     <paramref name="cancellationToken"/> (the host's shutdown deadline); a tick still running then ends on its own.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            cts = _cts;
            _loop = null;
            _cts = null;
        }

        if (loop is null)
        {
            return;
        }

        await cts!.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown, or the host's deadline passed while a retry tick is still running
        }
        finally
        {
            // dispose once the loop has really ended (immediately if it already did)
            _ = loop.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(PeriodicTimer periodicTimer, CancellationToken stoppingToken)
    {
        using var timer = periodicTimer;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    using var scope = serviceScopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IDefaultConfigurationCreatorService>();
                    await service.RetryFailedTenantsAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Unexpected error during failed tenant retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
