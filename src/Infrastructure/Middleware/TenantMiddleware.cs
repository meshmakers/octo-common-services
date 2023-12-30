using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.Infrastructure.Middleware;

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
        using var systemSession = await systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            var tenantRepository = tenantContext.GetTenantRepository();
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
        }
        else
        {
            var tenantRepository = systemContext.GetTenantRepository();
            context.Items[BackendCommon.TenantRepositoryName] = tenantRepository;
        }

        await systemSession.CommitTransactionAsync();

        // Call the next delegate/middleware in the pipeline
        await _next(context);
    }
}