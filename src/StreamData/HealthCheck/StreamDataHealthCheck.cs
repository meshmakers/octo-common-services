using Meshmakers.Octo.Services.Common.StreamData.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Services.Common.StreamData.HealthCheck;

/// <summary>
/// Health check for stream data database
/// </summary>
public class StreamDataHealthCheck(IStreamDataHealthCheckClient client) : IHealthCheck
{
    /// <summary>
    /// Health check for stream data database
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        return client.CheckHealthAsync();
    }
}