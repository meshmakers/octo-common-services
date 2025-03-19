using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Infrastructure;

public static class HttpContextAccessorExtensions
{
    public static async Task<ITenantRepository> GetTenantRepositoryAsync(this IHttpContextAccessor httpContextAccessor)
    {
        if (httpContextAccessor.HttpContext == null)
        {
            throw new OctoServiceException("Service scope is not created.");
        }

        var systemContext = httpContextAccessor.HttpContext.RequestServices.GetRequiredService<ISystemContext>();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(httpContextAccessor.GetTenantId()).ConfigureAwait(false);

        return tenantRepository;
    }

    public static string GetTenantId(this IHttpContextAccessor httpContextAccessor)
    {
        if (httpContextAccessor.HttpContext == null)
        {
            throw new OctoServiceException("Service scope is not created.");
        }

        var tenantId = httpContextAccessor.HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new OctoServiceException("TenantId is not found in the request.");
        }
        return tenantId;
    }
}