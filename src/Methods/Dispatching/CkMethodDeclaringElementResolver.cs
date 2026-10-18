using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>Finds the element that declares the method a request names (routing key of queue, registry and handler).</summary>
internal interface ICkMethodDeclaringElementResolver
{
    /// <summary>
    ///     The qualified, model-version-less id of the method on its declaring element, or the id of the request
    ///     unchanged when the CK cache does not know the target type or the method. Never throws.
    /// </summary>
    string Resolve(CkMethodRequest request);
}

/// <summary>
///     Resolves through the per-tenant CK cache: <c>CkTypeGraph.AllMethods</c> holds inherited methods with their
///     declaring type, and for a method that a type gets from an interface the highest type of the chain that implements
///     the interface (engine, <c>InheritanceResolver</c>). A call that targets a derived or an implementing type thus
///     routes to the queue of the declaring element.
/// </summary>
internal sealed class CkCacheMethodDeclaringElementResolver(
    ILogger<CkCacheMethodDeclaringElementResolver> logger,
    ICkCacheService? ckCache = null) : ICkMethodDeclaringElementResolver
{
    public string Resolve(CkMethodRequest request)
    {
        if (ckCache == null || !CkMethodIds.TryParse(request.MethodId, out _, out var methodId))
        {
            return request.MethodId;
        }

        try
        {
            if (!ckCache.IsTenantLoaded(request.TenantId) ||
                !ckCache.TryGetRtCkType(request.TenantId, new RtCkId<CkTypeId>(request.CkTypeId), out var type) ||
                !type.AllMethods.TryGetValue(methodId, out var method))
            {
                return request.MethodId;
            }

            return method.QualifiedMethodId;
        }
        catch (Exception e)
        {
            // A malformed type id or a cache fault must not fail the call: the id of the request is routed as given.
            logger.LogDebug(e, "Resolving the declaring element of CK method {MethodId} for type {CkTypeId} failed",
                request.MethodId, request.CkTypeId);
            return request.MethodId;
        }
    }
}
