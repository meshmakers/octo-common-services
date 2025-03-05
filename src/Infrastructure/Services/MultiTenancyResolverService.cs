using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public class MultiTenancyResolverService(IHttpContextAccessor httpContextAccessor, ISystemContext systemContext)
    : IMultiTenancyResolverService
{
    public ITenantRepository GetTenantRepository()
    {
        var tenantRepository = httpContextAccessor.HttpContext?.Items[BackendCommon.TenantRepositoryName] as ITenantRepository ??
                               systemContext.GetSystemTenantRepository();
        return tenantRepository;
    }

    public string GetTenantId()
    {
        var tenantId = httpContextAccessor.HttpContext?.Items[BackendCommon.TenantIdName] as string;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw TenantNotFoundException.TenantIdNotFound();
        }
        return tenantId;
    }
}