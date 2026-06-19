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
/// the MongoDB driver's command-event callbacks, so the headers reflect the real work done
/// for this request — regardless of whether the inner pipeline is GraphQL, REST, or a mix.
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
/// Auto-registers <see cref="MongoCommandSurfaceMiddleware"/> at the start of the ASP.NET Core
/// pipeline when <c>AddObservability()</c> is called, so consuming services don't need to
/// remember to wire it up explicitly. Placed near the top of the pipeline (but after exception
/// handling) so it captures Mongo work performed by every downstream middleware including
/// authentication and authorization.
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
