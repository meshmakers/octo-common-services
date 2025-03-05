using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public interface IMultiTenancyResolverService
{
    /// <summary>
    ///     Returns the tenant repository for the current request.
    /// </summary>
    /// <returns>The current tenant repository for the request.</returns>
    ITenantRepository GetTenantRepository();


    /// <summary>
    /// Returns the tenant id for the current request.
    /// </summary>
    /// <returns>The tenant id for the current request.</returns>
    string GetTenantId();
}