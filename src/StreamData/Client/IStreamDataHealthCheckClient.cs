using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Services.StreamData.Client;

/// <summary>
/// Client for checking the health of database connection.
/// </summary>
public interface IStreamDataHealthCheckClient
{
    /// <summary>
    /// Checks the health of the database connection.
    /// </summary>
    /// <returns></returns>
    public Task<HealthCheckResult> CheckHealthAsync();
}