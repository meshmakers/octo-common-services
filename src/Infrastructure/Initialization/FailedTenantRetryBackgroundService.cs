using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Background service that periodically retries tenant setup for two failure sources:
///     <list type="bullet">
///         <item>
///             Tenants whose deferred <see cref="IDefaultConfigurationCreatorService.StartDeferredTenantsAsync"/>
///             call failed (tracked in <see cref="FailedTenantRegistry"/>).
///         </item>
///         <item>
///             Tenants whose identity-data setup was deferred or rejected with a transient
///             condition during <c>SetupTenantAsync</c> (tracked in the standardized creator's
///             internal pending list).
///         </item>
///     </list>
/// </summary>
/// <remarks>
///     AB#4208 — the previous implementation gated the periodic call on
///     <c>failedTenantRegistry.HasFailedTenants</c>. That gate hid the standardized creator's
///     internal pending list from the timer: a service whose <c>StartTenantAsync</c> is a no-op
///     (e.g. MCP) never populated the DI-registered registry, so a tenant stuck in
///     <c>_pendingIdentityDataTenantIds</c> was never retried until the next pod restart.
///     <c>RetryFailedTenantsAsync</c> is itself self-gating (returns immediately when both
///     lists are empty), so dropping the outer gate carries no measurable cost.
/// </remarks>
internal class FailedTenantRetryBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<FailedTenantRetryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait before first retry to give other services time to become available
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(RetryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDefaultConfigurationCreatorService>();
                await service.RetryFailedTenantsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during failed tenant retry");
            }
        }
    }
}
