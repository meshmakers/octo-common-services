using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISystemContext systemContext)
    {
        // Load tenant repository
        var tenantId = context.GetTenantId();
        using var systemSession = await systemContext.GetSystemSessionAsync().ConfigureAwait(false);
        systemSession.StartTransaction();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
        }
        else
        {
            var tenantRepository = systemContext.GetTenantRepository();
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
        }

        await systemSession.CommitTransactionAsync().ConfigureAwait(false);

        // Call the next delegate/middleware in the pipeline
        await _next(context).ConfigureAwait(false);
    }
}