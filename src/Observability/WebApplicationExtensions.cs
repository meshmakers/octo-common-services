using Microsoft.AspNetCore.Builder;

namespace Observability;

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
        app.MapHealthChecks("/health");
    }
}