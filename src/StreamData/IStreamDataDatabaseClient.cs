using Meshmakers.Octo.StreamData.Dtos;

namespace Meshmakers.Octo.StreamData;

/// <summary>
/// Provides data access to a stream data database
/// </summary>
public interface IStreamDataDatabaseClient
{
    /// <summary>
    /// Insert a single datapoint into the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="datapoint"></param>
    /// <returns></returns>
    public Task InsertDataAsync(string tenantId, DataPointDto datapoint);

    /// <summary>
    /// Get data from the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="query"></param>
    /// <returns></returns>
    Task<List<DataPointDto>> GetDataAsync(string tenantId, string query);

    /// <summary>
    /// Insert multiple datapoints into the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="datapoints"></param>
    /// <returns></returns>
    public Task InsertDataAsync(string tenantId, IEnumerable<DataPointDto> datapoints);
}