using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

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
    /// Map the endpoints for observability
    /// </summary>
    /// <remarks>
    /// AB#5868: <c>/metrics</c> used to be mapped on the main listener, i.e. on the same port the public
    /// ingress forwards to, and handed out tenant ids, pipeline names and pool ids to anyone. It is now
    /// served only to connections that arrive on <see cref="PrometheusPortKey"/> — a port the Service and
    /// the Ingress never route to — so a wrong ingress rule can no longer re-expose it. Without the key
    /// the endpoint does not exist (404). Health endpoints are unaffected.
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
        });
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
}
