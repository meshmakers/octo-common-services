using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     AB#8228 — outermost middleware of every octo service: when a request fails with a MongoDB authentication
///     error (a tenant's database user was dropped or re-created while this process holds pooled connections that
///     lost their authentication), the tenant's cached repository clients are evicted so the next request
///     authenticates anew and succeeds without a restart. The exception is rethrown unchanged.
/// </summary>
/// <remarks>
///     Registered by <see cref="TenantPoolEvictionStartupFilter" /> through
///     <c>AddOctoServiceInfrastructure()</c>, so asset repository, communication controller, bot services and the
///     others get it without touching their pipelines. Errors that a framework layer turns into a response before
///     they reach this middleware (GraphQL) must call
///     <see cref="TenantDatabaseUserHealer.EvictOnAuthenticationFailureAsync" /> themselves.
/// </remarks>
internal sealed class TenantPoolEvictionMiddleware(RequestDelegate next, TenantDatabaseUserHealer? healer = null)
{
    private readonly TenantDatabaseUserHealer _healer = healer ?? TenantDatabaseUserHealer.Default;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (TenantDatabaseUserHealer.IsAuthenticationFailure(ex))
        {
            // Resolved lazily: a service hosting the infrastructure without the runtime engine has nothing to evict.
            var systemContext = context.RequestServices?.GetService<ISystemContext>();
            if (systemContext == null)
            {
                throw;
            }

            var logger = context.RequestServices?.GetService<ILogger<TenantPoolEvictionMiddleware>>();
            var tenantId = context.Items[InfrastructureCommon.TenantIdName] as string
                           ?? context.GetTenantId()
                           ?? context.GetTenantIdFromPath()
                           ?? systemContext.TenantId;
            await _healer.EvictOnAuthenticationFailureAsync(systemContext, tenantId,
                (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ex).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Puts <see cref="TenantPoolEvictionMiddleware" /> at the start of the pipeline of every octo service.</summary>
internal sealed class TenantPoolEvictionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<TenantPoolEvictionMiddleware>();
        next(app);
    };
}
