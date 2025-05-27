using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Implements an initialization service to create default configuration for tenants
/// </summary>
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

            var systemSession = await _systemContext.GetAdminSessionAsync().ConfigureAwait(false);
            systemSession.StartTransaction();

            var tenants = await _systemContext.GetChildTenantsAsync(systemSession).ConfigureAwait(false);
            foreach (var tenant in tenants.Items)
            {
                _logger.LogInformation("Initialize default configuration for tenant '{TenantId}'", tenant.TenantId);
                await _defaultConfigurationCreatorService.SetupAsync(tenant.TenantId).ConfigureAwait(false);
                _logger.LogInformation("Initialize default configuration for tenant '{TenantId}' done", tenant.TenantId);
            }
        }
    }
}