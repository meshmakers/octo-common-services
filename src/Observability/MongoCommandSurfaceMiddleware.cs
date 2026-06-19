using System.Globalization;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Observability;

/// <summary>
/// Per-request middleware that surfaces aggregate MongoDB cost back to the caller via response
/// headers:
/// <list type="bullet">
///   <item><c>X-Octo-MongoDb-Duration-Ms</c> — sum of every Mongo command's duration</item>
///   <item><c>X-Octo-MongoDb-Command-Count</c> — number of commands issued</item>
/// </list>
/// The accumulator is opened once per HTTP request and flows through <c>AsyncLocal</c> into
/// the MongoDB driver's command-event callbacks. Header values are captured at the
/// <c>Response.OnStarting</c> hook, so they cover every Mongo command that completes before
/// the response body starts flushing — i.e. work issued during the resolver / controller
/// pipeline, but not late writes performed after the response has been sent (which is unusual
/// but possible for a handler that does post-response cleanup).
/// </summary>
public sealed class MongoCommandSurfaceMiddleware(RequestDelegate next)
{
    internal const string DurationHeader = "X-Octo-MongoDb-Duration-Ms";
    internal const string CommandCountHeader = "X-Octo-MongoDb-Command-Count";

    public async Task InvokeAsync(HttpContext context)
    {
        using var _ = MongoRequestScope.Begin(out var stats);

        // Headers must be set before the response body starts flushing. OnStarting is the
        // only correct hook — writing headers post-flush throws InvalidOperationException.
        context.Response.OnStarting(() =>
        {
            // Defensive: another middleware may have already started the response if it
            // short-circuited; in that case OnStarting still fires but the headers
            // collection might be in a flushed state on some hosts.
            if (!context.Response.HasStarted)
            {
                context.Response.Headers[DurationHeader] =
                    ((long)stats.TotalMs).ToString(CultureInfo.InvariantCulture);
                context.Response.Headers[CommandCountHeader] =
                    stats.CommandCount.ToString(CultureInfo.InvariantCulture);
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}

/// <summary>
/// Auto-registers <see cref="MongoCommandSurfaceMiddleware"/> in the ASP.NET Core pipeline when
/// <c>AddObservability()</c> is called, so consuming services don't need to remember to wire it
/// up explicitly. The middleware runs early in the pipeline so it covers Mongo work performed
/// by downstream middleware including authentication and authorization. Note that
/// <see cref="IStartupFilter"/> does not guarantee a specific position relative to other
/// pipeline middleware or other startup filters — consumers that need a particular ordering
/// should register their own middleware explicitly.
/// </summary>
internal sealed class MongoCommandSurfaceStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<MongoCommandSurfaceMiddleware>();
            next(app);
        };
}
