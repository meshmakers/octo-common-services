using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Starts all tenants that were deferred during DefaultConfigurationInitializationService.
///     This runs after the distribution event hub has been started by EventHubStartupService (Order=20),
///     so that StartTenantAsync implementations can safely send commands via the bus.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class TenantStartupInitializationService(
    ILogger<TenantStartupInitializationService> logger,
    IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    : IAsyncInitializationService
{
    /// <summary>
    ///     Must be higher than EventHubStartupService (Order = 20)
    ///     so that the bus is available when StartTenantAsync is called.
    /// </summary>
    public int Order => 30;

    public async Task InitializeAsync()
    {
        logger.LogInformation("Starting deferred tenants after distribution event hub is available");
        await defaultConfigurationCreatorService.StartDeferredTenantsAsync().ConfigureAwait(false);
        logger.LogInformation("All deferred tenants started");
    }
}
