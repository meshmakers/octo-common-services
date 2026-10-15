using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Observability;

/// <summary>
/// Extensions for the <see cref="WebApplication"/> class.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Configuration key (environment variable <c>Observability__PrometheusPort</c>) of the dedicated,
    /// cluster-internal port that serves the Prometheus scrape endpoint <c>/metrics</c>. Unset or
    /// <c>0</c> (the default) means: no Prometheus endpoint at all.
    /// </summary>
    public const string PrometheusPortKey = "Observability:PrometheusPort";

    /// <summary>
    /// Name of the authentication scheme that must authenticate a caller of the detailed health report
    /// (<c>/health</c>): the <c>Bearer</c> (JWT) scheme every service registers for its APIs. Kept as a literal
    /// so this package needs no reference to the JwtBearer package.
    /// </summary>
    internal const string HealthReportScheme = "Bearer";

    /// <summary>
    /// Map the endpoints for observability
    /// </summary>
    /// <remarks>
    /// AB#5868: <c>/metrics</c> used to be mapped on the main listener, i.e. on the same port the public
    /// ingress forwards to, and handed out tenant ids, pipeline names and pool ids to anyone. It is now
    /// served only to connections that arrive on <see cref="PrometheusPortKey"/> — a port the Service and
    /// the Ingress never route to — so a wrong ingress rule can no longer re-expose it. Without the key
    /// the endpoint does not exist (404).
    /// <para>
    /// AB#6739: <c>/health</c> writes the full health report (check names, status, descriptions, CPU/memory
    /// figures) and used to answer anonymous callers on the public listener. It now requires a valid
    /// <see cref="HealthReportScheme"/> token (any authenticated caller, no role); anonymous callers get 401. The
    /// check runs inside the endpoint, so it does not depend on where the host calls <c>UseAuthorization()</c> (the
    /// mesh adapter never does) and fails closed (401) on hosts that register no <c>Bearer</c> scheme at all, such
    /// as the EDA and FinAPI adapters. <c>/healthz/live</c> and <c>/healthz/ready</c> (kubelet probes, plain
    /// status, no body) stay anonymous.
    /// </para>
    /// </remarks>
    /// <param name="app"></param>
    public static void MapObservability(this WebApplication app)
    {
        var prometheusPort = app.Configuration.GetValue<int>(PrometheusPortKey);
        if (prometheusPort > 0)
        {
            app.UseOpenTelemetryPrometheusScrapingEndpoint(context => IsPrometheusScrape(context, prometheusPort));
        }

        app.MapHealthChecks("/health", new HealthCheckOptions()
        {
            ResponseWriter = ResponseWriter.WriteResponse
        }).Add(RequireAuthenticatedCaller);
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = healthCheck => healthCheck.Tags.Contains("ready")
        });
        app.MapHealthChecks("/healthz/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });
    }

    /// <summary>
    /// A scrape is served only on the dedicated port. The decision uses the socket's local port
    /// (<see cref="ConnectionInfo.LocalPort"/>), never the <c>Host</c> header, because the header is
    /// caller-controlled and the port of the socket is not.
    /// </summary>
    internal static bool IsPrometheusScrape(HttpContext context, int prometheusPort)
    {
        return context.Connection.LocalPort == prometheusPort
               && context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireAuthenticatedCaller(EndpointBuilder endpoint)
    {
        var inner = endpoint.RequestDelegate;
        if (inner == null)
        {
            return;
        }

        endpoint.RequestDelegate = async context =>
        {
            if (await IsAuthenticatedAsync(context))
            {
                await inner(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = HealthReportScheme;
        };
    }

    internal static async Task<bool> IsAuthenticatedAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        if (schemes == null || await schemes.GetSchemeAsync(HealthReportScheme) == null)
        {
            return false;
        }

        var result = await context.AuthenticateAsync(HealthReportScheme);
        if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        context.User = result.Principal;
        return true;
    }
}
