namespace Meshmakers.Octo.Services.Common.Timeseries;

/// <summary>
/// Provides management operations to a time series database
/// </summary>
public interface ITimeSeriesDatabaseManagementClient
{
    /// <summary>
    /// Creates a table in a time series database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task CreateTimeSeriesTableIfNotExistAsync(string tenantId);

    /// <summary>
    /// Deletes a table in a time series database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DeleteTimeSeriesDatabaseAsync(string tenantId);
}