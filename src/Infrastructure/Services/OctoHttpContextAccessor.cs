using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Class to access the current HTTP context and retrieve tenant information.
/// </summary>
/// <param name="httpContextAccessor">HTTP context accessor to access the current HTTP context.</param>
public class OctoHttpContextAccessor(IHttpContextAccessor httpContextAccessor) : IOctoHttpContextAccessor
{
    public async Task<ITenantRepository> GetTenantRepositoryAsync()
    {
        if (httpContextAccessor.HttpContext == null)
        {
            throw new OctoServiceException("Service scope is not created.");
        }

        var systemContext = httpContextAccessor.HttpContext.RequestServices.GetRequiredService<ISystemContext>();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(GetTenantId()).ConfigureAwait(false);

        return tenantRepository;
    }

    public string GetTenantId()
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