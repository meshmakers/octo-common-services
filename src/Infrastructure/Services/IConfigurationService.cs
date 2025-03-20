namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Configuration service of an OctoMesh service
/// </summary>
public interface IConfigurationService : IDefaultConfigurationCreatorService
{
    /// <summary>
    /// Enables the service for a tenant
    /// </summary>
    /// <param name="tenantId">ID of the tenant</param>
    /// <returns></returns>
    Task EnableAsync(string tenantId);
    
    /// <summary>
    /// Disables the service for a tenant
    /// </summary>
    /// <param name="tenantId">ID of the tenant</param>
    /// <returns></returns>
    Task DisableAsync(string tenantId);
    
    /// <summary>
    /// Returns true if the service is enabled for a tenant
    /// </summary>
    /// <param name="tenantId">ID of the tenant</param>
    /// <returns>True if the current schema is enabled</returns>
    Task<bool> IsEnabledAsync(string tenantId);

    /// <summary>
    /// Returns true if the service can be enabled or disabled by tenant.
    /// </summary>
    /// <returns>True, when the service can be enabled or disabled by tenant</returns>
    bool CanBeEnabled();
}