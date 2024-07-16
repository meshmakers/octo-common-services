using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Common;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public class MultiTenancyResolverService : IMultiTenancyResolverService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISystemContext _systemContext;

    public MultiTenancyResolverService(IHttpContextAccessor httpContextAccessor, ISystemContext systemContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _systemContext = systemContext;
    }

    public ITenantRepository GetTenantRepository()
    {
        var tenantRepository = _httpContextAccessor.HttpContext?.Items[BackendCommon.TenantRepositoryName] as ITenantRepository ??
                               _systemContext.GetSystemTenantRepository();
        return tenantRepository;
    }
}