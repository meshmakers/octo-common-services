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
    /// <param name="tenantId"></param>
    /// <param name="ckId"></param>
    /// <param name="rtId"></param>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <param name="limit"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    Task<IEnumerable<DataPointDto>> GetDataAsync(string tenantId, string ckId, string rtId, DateTime from, DateTime to,
        int limit = 10, int offset = 0);
}