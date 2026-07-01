using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

internal class TenantMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Represents the system endpoints
    /// </summary>
    private static readonly List<string> SystemEndpoints =
    [
        "/system",
        "/signin-oidc",
        "/healthz"
    ];

    public async Task InvokeAsync(HttpContext context, ISystemContext systemContext,
        IConfigurationService configurationService)
    {
        // Check if the request is a system endpoint
        if (context.Request.Path.Value != null && SystemEndpoints.Contains(context.Request.Path.Value))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Load tenant repository
        var tenantId = context.GetTenantId();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            // Feature lifecycle endpoints (…/enable, …/disable) manage the enabled-state itself
            // and must never be blocked by the enabled-gate below (otherwise a disabled feature
            // could never be re-enabled via its own API — the AB#4287 regression).
            var path = context.Request.Path.Value;
            var isLifecycleEndpoint = path != null
                                      && (path.EndsWith("/enable", StringComparison.OrdinalIgnoreCase)
                                          || path.EndsWith("/disable", StringComparison.OrdinalIgnoreCase));

            // Check if the tenant exists
            var tenantRepository = await systemContext.TryFindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
            if (tenantRepository == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Check if the service can be enabled, check if the service is enabled for the tenant,
            // but allow access to system api endpoints and to the feature lifecycle endpoints
            if (configurationService.CanBeEnabled()
                && !isLifecycleEndpoint
                && !await configurationService.IsEnabledAsync(tenantId).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Items[InfrastructureCommon.TenantRepositoryName] = tenantRepository;
            context.Items[InfrastructureCommon.TenantIdName] = tenantRepository.TenantId;
        }
        else
        {
            var tenantRepository = systemContext.GetTenantRepository();
            context.Items[InfrastructureCommon.TenantRepositoryName] = tenantRepository;
            context.Items[InfrastructureCommon.TenantIdName] = tenantRepository.TenantId;
        }

        // Call the next delegate/middleware in the pipeline
        await next(context).ConfigureAwait(false);
    }
}