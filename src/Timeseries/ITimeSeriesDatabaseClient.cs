using Meshmakers.Octo.Services.Common.Timeseries.Dtos;

namespace Meshmakers.Octo.Services.Common.Timeseries;

/// <summary>
/// Provides data access to a time series database
/// </summary>
public interface ITimeSeriesDatabaseClient
{
    /// <summary>
    /// Insert a single datapoint into the time series database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="datapoint"></param>
    /// <returns></returns>
    public Task InsertDataAsync(string tenantId, DataPointDto datapoint);

    /// <summary>
    /// Get data from the time series database.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    Task<List<DataPointDto>> GetDataAsync(string query);
}