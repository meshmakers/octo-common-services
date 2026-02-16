using Meshmakers.Octo.Services.StreamData.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Services.StreamData.Client;

internal interface ICrateDbConnectionAccess
{
    /// <summary>
    /// Returns a connection to the crate db.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    NpgsqlConnection CreateConnection(string tenantId);
}

internal class CrateDbConnectionAccess(
    IOptions<StreamDataConfiguration> options,
    ILogger<CrateDbConnectionAccess> logger)
    : ICrateDbConnectionAccess
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public NpgsqlConnection CreateConnection(string tenantId)
    {
        var cacheKey = NormalizeTenantId(tenantId);
        _semaphore.Wait();

        var dataSource = _cache.GetOrCreate<NpgsqlDataSource>(cacheKey, f =>
        {
            var datasourceId = Guid.NewGuid();

            f.SlidingExpiration = options.Value.ConnectionCacheDuration;
            f.RegisterPostEvictionCallback(DisposeCallback, datasourceId);

            var connectionString = options.Value.ConnectionString;

            logger.LogInformation("Creating database datasource '{DatasourceId}' for tenant {TenantId}",
                datasourceId,
                tenantId);
            logger.LogDebug("Connection string: {ConnectionString}", connectionString);

            var csb = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // CrateDB does not support ROLLBACK which Npgsql sends during pooled connection
                // reset (NpgsqlConnector.Reset). NoResetOnClose only prevents DISCARD ALL but NOT
                // the ROLLBACK that Npgsql unconditionally sends when TransactionStatus != Idle.
                // Disabling pooling prevents Reset from being called entirely on connection close.
                NoResetOnClose = true,
                Pooling = false
            };
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
            dataSourceBuilder.EnableDynamicJson([typeof(IReadOnlyDictionary<string, object?>)]);
            var dataSource = dataSourceBuilder.Build();

            return dataSource;
        });
        
        _semaphore.Release();
        
        if (dataSource == null)
        {
            throw StreamDataException.CouldNotCreateDatabaseConnection();
        }

        return dataSource.OpenConnection();
    }

    private void DisposeCallback(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is not Guid datasourceId || value is not NpgsqlDataSource datasource)
        {
            throw new InvalidOperationException("Invalid state");
        }

        logger.LogInformation("Disposing datasource '{DatasourceId}' for tenant '{TenantId}'", 
            datasourceId,
            key);

        datasource.Dispose();
    }

    private static string NormalizeTenantId(string tenantId) => tenantId.ToLowerInvariant();
}