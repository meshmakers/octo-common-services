using System.Diagnostics;
using Dapper;
using Meshmakers.Octo.Services.Common.Timeseries.Configuration;
using Meshmakers.Octo.Services.Common.Timeseries.Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Services.Common.Timeseries;

/// <summary>
/// Client for interacting with the time series database.
/// </summary>
internal class CrateDatabaseClient : ITimeSeriesDatabaseClient, ITimeSeriesDatabaseManagementClient
{
    private readonly string _connectionString;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="options"></param>
    public CrateDatabaseClient(IOptions<TimeSeriesConfiguration> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.Value.TimeSeriesConnectionString);
        
        _connectionString = options.Value.TimeSeriesConnectionString;
        
        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, object>>());
        SqlMapper.AddTypeHandler(new CkIdTypeHandler());
        SqlMapper.AddTypeHandler(new OctoIdTypeHandler());
    }

    public async Task<IEnumerable<DataPointDto>> GetDataAsync(string tenantId, string ckId, string rtId,
        DateTime from, DateTime to, int limit = 10, int offset = 0)
    {
        var query = string.Format(Queries.SelectTimeSeriesDataByRtIdAndTimestamp, tenantId, ckId);
        await using var connection = await CreateConnection();

        var result = await connection.QueryAsync<DapperSerializableDatapoint>(query, new { rtId, from, to, limit, offset });

        var datapoints = new List<DataPointDto>();
        
        foreach (var r in result)
        {
            datapoints.Add(new DataPointDto()
            {
                ExternalId = r.ExternalId,
                AdapterReceivedTimestamp = r.AdapterReceivedTimestamp,
                DataRtId = r.DataRtId,
                PlugId = r.PlugId,
                Timestamp = r.Timestamp,
                Values = r.Values.Value,
            });
        }

        return datapoints;
    }

    public async Task InsertDataAsync(string tenantId, DataPointDto datapoint)
    {
        var query = string.Format(Queries.InsertTimeSeriesEntry, tenantId);
        await using var connection = await CreateConnection();

        var data = new Json<Dictionary<string, object?>>(datapoint.Values);
        
        var result = await connection.ExecuteAsync(query,
            new
            {
                rtId = datapoint.DataRtId.RtId, 
                ckId = datapoint.DataRtId.CkTypeId,
                timestamp = datapoint.Timestamp,
                data,
            });
    }

    public async Task CreateTimeSeriesTableIfNotExistAsync(string tenantId)
    {
        await using var connection = await CreateConnection();
        var result = await connection.ExecuteAsync(string.Format(Queries.CreateTableIfNotExists, tenantId));
    }

    public async Task DeleteTimeSeriesDatabaseAsync(string tenantId)
    {
        await using var connection = await CreateConnection();
        var result = await connection.ExecuteAsync(string.Format(Queries.DeleteTableIfExists, tenantId));
    }
    
    private ValueTask<NpgsqlConnection> CreateConnection()
    {
        var csb = new NpgsqlConnectionStringBuilder(_connectionString);

        var v = typeof(NpgsqlDataSourceBuilder).Assembly.GetName().Version;
        var expectedVersion = new Version("8.0.1.0");
        Debug.Assert(v == expectedVersion,
            "After updating to newer version of npgsql, check if the following issue is in the release notes: https://github.com/npgsql/npgsql/issues/5503#issuecomment-1883801663");
        csb.ServerCompatibilityMode = ServerCompatibilityMode.NoTypeLoading;

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);


        var dataSource = dataSourceBuilder.Build();
        return dataSource.OpenConnectionAsync();
    }
}