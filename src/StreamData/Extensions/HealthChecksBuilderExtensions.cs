using Meshmakers.Octo.Services.StreamData.HealthCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Services.StreamData.Extensions;

/// <summary>
/// Extension methods for the health check builder.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    private static readonly string[] StreamDataTags = ["streamdata"];

    /// <summary>
    /// Adds a health check for the stream data database.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IHealthChecksBuilder AddStreamDataHealthCheck(this IHealthChecksBuilder builder)
    {
        builder.AddCheck<StreamDataHealthCheck>(
            "StreamData", 
            HealthStatus.Unhealthy, 
            StreamDataTags);
        return builder;
    }
}