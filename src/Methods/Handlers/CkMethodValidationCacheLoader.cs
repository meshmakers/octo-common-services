using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Loads a tenant's CK cache through the tenant context of the service (AB#8969). A host without an
///     <see cref="ISystemContext" /> (tests, a service without tenant contexts) cannot load; it throws, and the validation
///     waits for whoever loads the cache until its deadline.
/// </summary>
internal sealed class CkMethodValidationCacheLoader(IServiceProvider services) : ICkMethodValidationCacheLoader
{
    public async Task LoadAsync(string tenantId)
    {
        var systemContext = (ISystemContext?)services.GetService(typeof(ISystemContext)) ??
                            throw new InvalidOperationException(
                                "This service has no tenant context (ISystemContext) to load the CK cache from");
        var tenant = await systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);
        // loads only when the cache is not loaded; may wait for a running CK model import (AB#8677)
        await tenant.LoadCacheForTenantAsync().ConfigureAwait(false);
    }
}
