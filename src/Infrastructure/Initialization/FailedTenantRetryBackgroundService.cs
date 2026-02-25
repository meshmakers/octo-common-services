using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Background service that periodically retries startup for tenants that failed during
///     <see cref="IDefaultConfigurationCreatorService.StartDeferredTenantsAsync"/>.
/// </summary>
internal class FailedTenantRetryBackgroundService(
    FailedTenantRegistry failedTenantRegistry,
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
            if (!failedTenantRegistry.HasFailedTenants)
            {
                continue;
            }

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
