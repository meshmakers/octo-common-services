namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Interface used to creating basic configuration for a tenant
/// </summary>
public interface IDefaultConfigurationCreatorService
{
    /// <summary>
    ///     Setups the default configuration
    /// </summary>
    /// <param name="tenantId">The tenant id, if null the system tenant is used.</param>
    /// <returns></returns>
    Task SetupAsync(string? tenantId);
}