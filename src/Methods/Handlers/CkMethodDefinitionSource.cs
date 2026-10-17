using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>Where the startup validation finds method definitions.</summary>
internal interface ICkMethodDefinitionSource
{
    /// <summary>Tenants searched, for the error message.</summary>
    IReadOnlyList<string> TenantIds { get; }

    /// <summary>The method with the qualified (model-version-less) id, or null when no searched tenant has it.</summary>
    CkMethodDto? Find(string qualifiedMethodId);
}

/// <summary>Reads the method definitions from the per-tenant CK cache of the service.</summary>
internal sealed class CkCacheMethodDefinitionSource : ICkMethodDefinitionSource
{
    private readonly ICkCacheService _ckCache;

    public CkCacheMethodDefinitionSource(ICkCacheService ckCache, IOptions<CkMethodHandlerOptions> options,
        IOptions<OctoSystemConfiguration> systemOptions)
    {
        _ckCache = ckCache;
        TenantIds = options.Value.ValidationTenantIds.Count > 0
            ? options.Value.ValidationTenantIds.ToList()
            : [systemOptions.Value.SystemTenantId];
    }

    public IReadOnlyList<string> TenantIds { get; }

    public CkMethodDto? Find(string qualifiedMethodId)
    {
        if (!CkMethodIds.TryParse(qualifiedMethodId, out var declaringTypeId, out var methodId))
        {
            return null;
        }

        foreach (var tenantId in TenantIds)
        {
            if (!_ckCache.IsTenantLoaded(tenantId))
            {
                continue;
            }

            if (_ckCache.TryGetRtCkType(tenantId, declaringTypeId, out var type) &&
                type.AllMethods.TryGetValue(methodId, out var method))
            {
                return method.Definition;
            }
        }

        return null;
    }
}
