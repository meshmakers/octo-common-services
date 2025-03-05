using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISystemContext systemContext)
    {
        // Load tenant repository
        var tenantId = context.GetTenantId();
        using var systemSession = await systemContext.GetAdminSessionAsync().ConfigureAwait(false);
        systemSession.StartTransaction();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
            context.Items[BackendCommon.TenantIdName] = tenantRepository.TenantId;
        }
        else
        {
            var tenantRepository = systemContext.GetTenantRepository();
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
            context.Items[BackendCommon.TenantIdName] = tenantRepository.TenantId;
        }

        await systemSession.CommitTransactionAsync().ConfigureAwait(false);

        // Call the next delegate/middleware in the pipeline
        await next(context).ConfigureAwait(false);
    }
}