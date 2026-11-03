using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Hosting;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     AB#8228 — evicts the tenant's cached repository clients when a request failed with a MongoDB authentication
///     error (a tenant's database user was dropped or re-created while this process holds pooled connections that
///     lost their authentication), so the next request authenticates anew and succeeds without a restart.
/// </summary>
/// <remarks>
///     One middleware is not enough: <c>UseDeveloperExceptionPage()</c> (local stack) and <c>UseExceptionHandler()</c>
///     turn the exception into a 500 response inside the pipeline, so it never reaches an outer middleware (round 2:
///     the asset repository never evicted although the middleware was registered). The trigger therefore sits in
///     three places that all see the exception: this middleware (no exception layer, production Kestrel),
///     <see cref="TenantPoolEvictionExceptionFilter" /> (MVC controllers, inside the exception layers) and
///     <see cref="OctoExceptionHandler" /> (<c>UseExceptionHandler</c>). They share the per-tenant rate limit of the
///     healer, so two of them firing for one failure evict once. GraphQL errors are a fourth place (the asset
///     repository's own error mapping) and call <see cref="TenantDatabaseUserHealer.EvictOnAuthenticationFailureAsync" />
///     themselves.
/// </remarks>
internal static class TenantPoolEviction
{
    /// <summary>Evicts the tenant addressed by the request when <paramref name="cause" /> is an authentication failure.</summary>
    public static async Task EvictAsync(HttpContext context, Exception cause, TenantDatabaseUserHealer healer)
    {
        if (!TenantDatabaseUserHealer.IsAuthenticationFailure(cause))
        {
            return;
        }

        // Resolved lazily: a service hosting the infrastructure without the runtime engine has nothing to evict.
        var systemContext = context.RequestServices?.GetService<ISystemContext>();
        if (systemContext == null)
        {
            return;
        }

        var logger = context.RequestServices?.GetService<ILogger<TenantPoolEvictionMiddleware>>();
        var tenantId = context.Items[InfrastructureCommon.TenantIdName] as string
                       ?? context.GetTenantId()
                       ?? context.GetTenantIdFromPath()
                       ?? systemContext.TenantId;
        await healer.EvictOnAuthenticationFailureAsync(systemContext, tenantId,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cause).ConfigureAwait(false);
    }
}

/// <summary>Outermost middleware of every octo service; the exception is rethrown unchanged.</summary>
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
            await TenantPoolEviction.EvictAsync(context, ex, _healer).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
///     MVC exception filter: sees the exception of a controller action before any exception layer swallows it and
///     leaves it unhandled (the response stays the layer's 500).
/// </summary>
internal sealed class TenantPoolEvictionExceptionFilter(TenantDatabaseUserHealer? healer = null) : IAsyncExceptionFilter
{
    private readonly TenantDatabaseUserHealer _healer = healer ?? TenantDatabaseUserHealer.Default;

    public Task OnExceptionAsync(ExceptionContext context) =>
        TenantPoolEviction.EvictAsync(context.HttpContext, context.Exception, _healer);
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
