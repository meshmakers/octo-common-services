namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Configuration service of the reporting service.
/// </summary>
public interface IConfigurationService : IDefaultConfigurationCreatorService
{
    /// <summary>
    /// Enables reporting for a tenant
    /// </summary>
    /// <param name="tenantId">ID of the tenant</param>
    /// <returns></returns>
    Task EnableAsync(string tenantId);
    
    /// <summary>
    /// Disables the reporting for a tenant
    /// </summary>
    /// <param name="tenantId">ID of the tenant</param>
    /// <returns></returns>
    Task DisableAsync(string tenantId);
    
    /// <summary>
    /// Returns true if the reporting is enabled for a tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<bool> IsEnabledAsync(string tenantId);
}