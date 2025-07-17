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
            throw OctoServiceException.HttpContextNotCreated();
        }

        var systemContext = httpContextAccessor.HttpContext.RequestServices.GetRequiredService<ISystemContext>();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(GetTenantId()).ConfigureAwait(false);

        return tenantRepository;
    }

    public string GetTenantId()
    {
        if (httpContextAccessor.HttpContext == null)
        {
            throw OctoServiceException.HttpContextNotCreated();
        }

        var tenantId = httpContextAccessor.HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw OctoServiceException.TenantIdNotFound();
        }
        return tenantId;
    }

    public T GetRequiredService<T>() where T : notnull
    {
        if (httpContextAccessor.HttpContext == null)
        {
            throw OctoServiceException.HttpContextNotCreated();
        }

        return httpContextAccessor.HttpContext.RequestServices.GetRequiredService<T>();
    }
}