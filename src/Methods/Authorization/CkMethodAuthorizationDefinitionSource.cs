using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>Where the authorization filter finds the definition of the method a request names.</summary>
public interface ICkMethodAuthorizationDefinitionSource
{
    /// <summary>
    ///     The definition of the method, or <c>null</c> when the tenant, the type or the method is unknown (the filter then
    ///     refuses the call: nothing can be verified). Never throws.
    /// </summary>
    /// <param name="tenantId">Tenant of the request.</param>
    /// <param name="ckTypeId">Concrete CK type of the target (<c>Model/Type</c>).</param>
    /// <param name="qualifiedMethodId">Qualified id of the method on its declaring element.</param>
    CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId);
}

/// <summary>
///     Reads the definition from the per-tenant CK cache: first from <c>AllMethods</c> of the concrete target type
///     (which holds inherited and interface methods), then from the declaring type.
/// </summary>
public sealed class CkCacheMethodAuthorizationDefinitionSource(
    ICkCacheService ckCache,
    ILogger<CkCacheMethodAuthorizationDefinitionSource> logger) : ICkMethodAuthorizationDefinitionSource
{
    /// <inheritdoc />
    public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId)
    {
        try
        {
            if (!CkMethodIds.TryParse(qualifiedMethodId, out var declaringTypeId, out var methodId) ||
                !ckCache.IsTenantLoaded(tenantId))
            {
                return null;
            }

            if (ckCache.TryGetRtCkType(tenantId, new RtCkId<CkTypeId>(ckTypeId), out var concrete) &&
                concrete.AllMethods.TryGetValue(methodId, out var inherited) &&
                string.Equals(inherited.QualifiedMethodId, qualifiedMethodId, StringComparison.Ordinal))
            {
                return inherited.Definition;
            }

            return ckCache.TryGetRtCkType(tenantId, declaringTypeId, out var declaring) &&
                   declaring.AllMethods.TryGetValue(methodId, out var method)
                ? method.Definition
                : null;
        }
        catch (Exception e)
        {
            // A malformed type id or a cache fault: unknown, and the filter refuses.
            logger.LogDebug(e, "Reading the definition of CK method {MethodId} for type {CkTypeId} failed", qualifiedMethodId,
                ckTypeId);
            return null;
        }
    }
}
