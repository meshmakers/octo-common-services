using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
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

        await EvictCoreAsync(context, healer).ConfigureAwait(false);
    }

    /// <summary>Evicts the tenant addressed by the request; the caller has classified the failure already.</summary>
    public static async Task EvictCoreAsync(HttpContext context, TenantDatabaseUserHealer healer)
    {
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
        await healer.EvictAfterAuthenticationFailureAsync(systemContext, tenantId,
            (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).ConfigureAwait(false);
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

/// <summary>
///     AB#8228 round 3 — central hook for controllers that catch the exception themselves and answer
///     <c>StatusCode(500, new InternalServerErrorDto(ex.Message))</c> (ModelsController, ...): no exception reaches the
///     middleware, the exception filter or the exception handler, only the 500 result with the message. This result
///     filter inspects 5xx <see cref="ObjectResult" />s (message of an API error DTO, <c>ProblemDetails.Detail</c>
///     or a plain string) and evicts when the message is a MongoDB authentication failure
///     (<see cref="TenantDatabaseUserHealer.IsAuthenticationFailureMessage" />). The result is not changed.
/// </summary>
internal sealed class TenantPoolEvictionResultFilter(TenantDatabaseUserHealer? healer = null) : IAsyncAlwaysRunResultFilter
{
    private readonly TenantDatabaseUserHealer _healer = healer ?? TenantDatabaseUserHealer.Default;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { StatusCode: >= 500 } result &&
            TenantDatabaseUserHealer.IsAuthenticationFailureMessage(ExtractMessage(result.Value)))
        {
            await TenantPoolEviction.EvictCoreAsync(context.HttpContext, _healer).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    private static string? ExtractMessage(object? value) => value switch
    {
        null => null,
        string text => text,
        Microsoft.AspNetCore.Mvc.ProblemDetails problem => problem.Detail ?? problem.Title,
        _ => value.GetType().GetProperty("Message")?.GetValue(value) as string
    };
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
