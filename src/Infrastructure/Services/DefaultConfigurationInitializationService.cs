using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Implements an initialization service to create default configuration for tenants
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class DefaultConfigurationInitializationService : IAsyncInitializationService
{
    private readonly ILogger<DefaultConfigurationInitializationService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    /// <param name="defaultConfigurationCreatorService"></param>
    public DefaultConfigurationInitializationService(ILogger<DefaultConfigurationInitializationService> logger, 
        ISystemContext systemContext, IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public int Order => 10;

    public async Task InitializeAsync()
    {
        // Defer tenant start until the distribution event hub is available.
        // StartTenantAsync may send commands via the bus (e.g. RemoveRecurringJobsByScheduleGroup),
        // so it must run after EventHubStartupService (Order=20) has started the bus.
        // TenantStartupInitializationService (Order=30) will call StartDeferredTenantsAsync.
        _defaultConfigurationCreatorService.DeferTenantStart = true;

        // Do global initialization here
        _logger.LogInformation("Initialize default configuration");
        await _defaultConfigurationCreatorService.InitializeAsync().ConfigureAwait(false);
        _logger.LogInformation("Initialize default configuration done");

        // Call for system tenant
        _logger.LogInformation("Initialize default configuration for system tenant '{TenantId}'", _systemContext.TenantId);
        await _defaultConfigurationCreatorService.SetupAsync(_systemContext.TenantId).ConfigureAwait(false);
        _logger.LogInformation("Initialize default configuration for system tenant '{TenantId}' done", _systemContext.TenantId);

        // Call for all child tenants
        _logger.LogInformation("Initialize default configuration for child tenants of '{TenantId}'", _systemContext.TenantId);
        if (await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            // Read the tenant list and close the session/transaction before iterating.
            // Keeping the transaction open while calling SetupAsync for each tenant would
            // cause MongoDB IX lock contention on octosystem.RtEntity_SystemTenant,
            // since SetupAsync also accesses that collection.
            List<OctoTenant> tenantList;
            using (var systemSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false))
            {
                systemSession.StartTransaction();
                var tenants = await _systemContext.GetChildTenantsAsync(systemSession).ConfigureAwait(false);
                tenantList = tenants.Items.ToList();
                await systemSession.CommitTransactionAsync().ConfigureAwait(false);
            }

            foreach (var tenant in tenantList)
            {
                _logger.LogInformation("Initialize default configuration for tenant '{TenantId}'", tenant.TenantId);
                await _defaultConfigurationCreatorService.SetupAsync(tenant.TenantId).ConfigureAwait(false);
                _logger.LogInformation("Initialize default configuration for tenant '{TenantId}' done", tenant.TenantId);
            }
        }
    }
}