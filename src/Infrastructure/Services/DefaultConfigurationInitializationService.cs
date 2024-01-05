using Meshmakers.Octo.Services.Infrastructure.Initialization;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Implements an initialization service to create default configuration for tenants
/// </summary>
public class DefaultConfigurationInitializationService : IAsyncInitializationService
{
    private readonly IDefaultConfigurationCreatorService _defaultConfigurationCreatorService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="defaultConfigurationCreatorService"></param>
    public DefaultConfigurationInitializationService(IDefaultConfigurationCreatorService defaultConfigurationCreatorService)
    {
        _defaultConfigurationCreatorService = defaultConfigurationCreatorService;
    }

    public int Order => 10;

    public Task InitializeAsync()
    {
        return _defaultConfigurationCreatorService.SetupAsync(null);
    }
}