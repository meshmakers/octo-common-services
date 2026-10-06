using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

internal class TenantMiddleware(RequestDelegate next)
{
    /// <summary>
    ///     The one spelling of the tenant dimension (AB#5478 §2.2). The estate had three — this one
    ///     on the workload metrics, a bare <c>tenant</c> on the CrateDB metrics and
    ///     <c>streamdata.tenant</c> on the StreamData spans — which meant no query could group a
    ///     tenant's traces, metrics and logs together. Do not add a fourth.
    /// </summary>
    private const string TenantAttributeName = "octo.tenant.id";

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

            TagTenant(tenantRepository.TenantId);

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
            TagTenant(tenantRepository.TenantId);
        }

        // Call the next delegate/middleware in the pipeline
        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    ///     Puts the addressed tenant on the request's span, so every signal of the request can be
    ///     filtered and grouped by tenant (AB#5478 §2.2).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Per-tenant workloads get this as a resource attribute from their chart and need no
    ///         code. The shared services cannot: the tenant changes per request, so it has to be set
    ///         where the request is resolved — here, the one place every tenant-addressed route in
    ///         every service passes through.
    ///     </para>
    ///     <para>
    ///         The value is the repository's canonical <c>TenantId</c> and never the raw route
    ///         segment. Tagging before resolution would let a typo or a probe write an arbitrary
    ///         string into the telemetry, and anything derived from spans would carry it.
    ///     </para>
    ///     <para>
    ///         Called before the enabled-gate above rather than after it, so a 403 on a disabled
    ///         feature stays attributable to the tenant that asked — that is one of the cases this
    ///         dimension exists for. A 404 for a tenant that does not exist is deliberately not
    ///         tagged: there is no tenant to attribute it to.
    ///     </para>
    ///     <para>
    ///         🔴 Only the span is tagged. The log records of this request do NOT carry the tenant
    ///         yet, and an <c>ILogger.BeginScope</c> here would not change that: the NLog layout in
    ///         use renders no scope properties, so it would be provably inert while looking like
    ///         coverage. The log side arrives with the structured layout in AB#5478 §2.3 — add the
    ///         scope in the same change that makes it render, not before.
    ///     </para>
    /// </remarks>
    private static void TagTenant(string tenantId)
    {
        // Null when nothing listens to the ASP.NET Core ActivitySource — local runs without the
        // injected auto-instrumentation and without the in-process SDK. Not an error.
        Activity.Current?.SetTag(TenantAttributeName, tenantId);
    }
}