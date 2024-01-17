using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Implements an initialization service to create default configuration for tenants
/// </summary>
public class DefaultConfigurationInitializationService : IAsyncInitializationService
{
    private readonly ISystemContext _systemContext;
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext"></param>
    /// <param name="defaultConfigurationCreatorService"></param>
    public DefaultConfigurationInitializationService(ISystemContext systemContext, IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _systemContext = systemContext;
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public int Order => 10;

    public async Task InitializeAsync()
    {
        // Call for system tenant
        await _defaultConfigurationCreatorService.SetupAsync(_systemContext.TenantId);

        // Call for all child tenants
        if (await _systemContext.IsSystemTenantExistingAsync())
        {
            var systemSession = await _systemContext.GetSystemSessionAsync();
            systemSession.StartTransaction();

            var tenants = await _systemContext.GetChildTenantsAsync(systemSession);
            foreach (var tenant in tenants.Items)
            {
                await _defaultConfigurationCreatorService.SetupAsync(tenant.TenantId);
            }
        }
    }
}