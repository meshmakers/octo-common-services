using Meshmakers.Octo.Services.Common.StreamData.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Services.Common.StreamData.Client;

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

    public NpgsqlConnection CreateConnection(string tenantId)
    {
        var cacheKey = NormalizeTenantId(tenantId);

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

            var csb = new NpgsqlConnectionStringBuilder(connectionString);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
            var dataSource = dataSourceBuilder.Build();

            return dataSource;
        });
        
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