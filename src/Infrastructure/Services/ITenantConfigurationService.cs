namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Represents a service to update the tenant configuration
/// </summary>
public interface ITenantConfigurationService
{
    /// <summary>
    /// Updates the tenant configuration
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="configurationName">Configuration name that is updated</param>
    /// <returns></returns>
    Task UpdateAsync(string tenantId, string configurationName);
}