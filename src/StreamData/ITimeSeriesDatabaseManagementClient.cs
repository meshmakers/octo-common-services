namespace Meshmakers.Octo.Services.Common.StreamData;

/// <summary>
/// Provides management operations to a stream data database
/// </summary>
public interface IStreamDataDatabaseManagementClient
{
    /// <summary>
    /// Creates a table in a stream data database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task CreateStreamDataTableIfNotExistAsync(string tenantId);

    /// <summary>
    /// Deletes a table in a stream data database for a given tenant.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task DeleteStreamDataDatabaseAsync(string tenantId);
}