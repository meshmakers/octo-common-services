using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Interface to access the current HTTP context and retrieve tenant information.
/// </summary>
public interface IOctoHttpContextAccessor
{
    /// <summary>
    /// Gets the tenant repository for the current HTTP context.
    /// </summary>
    /// <returns></returns>
    Task<ITenantRepository> GetTenantRepositoryAsync();

    /// <summary>
    /// Gets the tenant ID from the current HTTP context.
    /// </summary>
    /// <returns></returns>
    string GetTenantId();
}