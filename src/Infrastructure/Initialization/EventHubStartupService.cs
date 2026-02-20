using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.Initialization;

/// <summary>
///     Starts the distribution event hub after tenant initialization is complete.
///     This prevents message consumers from processing tenant events while
///     the initial tenant setup is still in progress, which would cause
///     MongoDB lock contention on shared collections.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class EventHubStartupService(
    ILogger<EventHubStartupService> logger,
    IEventHubControl eventHubControl)
    : IAsyncInitializationService
{
    /// <summary>
    ///     Must be higher than DefaultConfigurationInitializationService (Order = 10)
    ///     so the bus starts only after all tenants are initialized.
    /// </summary>
    public int Order => 20;

    public async Task InitializeAsync()
    {
        logger.LogInformation("Starting distribution event hub after tenant initialization");
        await eventHubControl.StartAsync().ConfigureAwait(false);
        logger.LogInformation("Distribution event hub started");
    }
}
