namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Interface used to creating basic configuration for a tenant
/// </summary>
public interface IDefaultConfigurationCreatorService
{
    /// <summary>
    ///     Initializes the default configuration
    /// </summary>
    /// <returns></returns>
    Task InitializeAsync();
    
    /// <summary>
    ///     Setups the default configuration for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant id, if null the system tenant is used.</param>
    /// <returns></returns>
    Task SetupAsync(string tenantId);
}