using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public class MultiTenancyResolverService(IHttpContextAccessor httpContextAccessor, ISystemContext systemContext)
    : IMultiTenancyResolverService
{
    public ITenantRepository GetTenantRepository()
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return systemContext.GetSystemTenantRepository();
        }

        var tenantRepository = httpContext.Items[InfrastructureCommon.TenantRepositoryName] as ITenantRepository;
        if (tenantRepository != null)
        {
            return tenantRepository;
        }

        // If the route contains a tenantId but no repository was resolved,
        // the tenant middleware failed to set it — this is an error.
        var routeTenantId = httpContext.GetRouteValue(InfrastructureCommon.TenantIdRoute) as string;
        if (!string.IsNullOrWhiteSpace(routeTenantId))
        {
            throw TenantNotFoundException.TenantIdNotFound();
        }

        // No tenantId in route (e.g., OIDC discovery endpoints) — fall back to system tenant.
        return systemContext.GetSystemTenantRepository();
    }

    public string GetTenantId()
    {
        var tenantId = httpContextAccessor.HttpContext?.Items[InfrastructureCommon.TenantIdName] as string;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw TenantNotFoundException.TenantIdNotFound();
        }
        return tenantId;
    }
}