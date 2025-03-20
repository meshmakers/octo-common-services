using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISystemContext systemContext,
        IConfigurationService configurationService)
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


        // Check if the service can be enabled, check if the service is enabled for the tenant,
        // but allow access to system api endpoints
        if (configurationService.CanBeEnabled()
            && !await configurationService.IsEnabledAsync(tenantId ?? systemContext.TenantId).ConfigureAwait(false)
            && (!context.Request.Path.Value?.StartsWith("/system") ?? false))
        {
            await systemSession.CommitTransactionAsync().ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await systemSession.CommitTransactionAsync().ConfigureAwait(false);

        // Call the next delegate/middleware in the pipeline
        await next(context).ConfigureAwait(false);
    }
}