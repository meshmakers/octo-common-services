using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Services.Observability;

/// <summary>
/// Extensions for the <see cref="WebApplication"/> class.
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Map the endpoints for observability
    /// </summary>
    /// <param name="app"></param>
    public static void MapObservability(this WebApplication app)
    {
        app.MapPrometheusScrapingEndpoint();
        app.MapHealthChecks("/health", new HealthCheckOptions()
        {
            ResponseWriter = ResponseWriter.WriteResponse
        });
    }
}