using System.Diagnostics;
using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
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

    public async Task<IEnumerable<DataPointDto>> GetDataAsync(string query)
    {
        await using var connection = await CreateConnection();

        var queryResult = await connection.QueryAsync(query);

        var dataPointDtos = new List<DataPointDto>();

        foreach (var entry in queryResult)
        {
            if (entry is not IDictionary<string, object?> result)
            {
                continue;
            }

            var dp = new DataPointDto(result.ToDictionary());

            if (result.TryGetValue(Constants.Timestamp, out var timestamp))
            {
                dp.Timestamp = (DateTime)timestamp!;
            }

            if (result.TryGetValue(Constants.RtId, out var rtId) && result.TryGetValue(Constants.CkId, out var ckId))
            {
                var rtEntityId = new RtEntityId(ckId as string ?? "", new OctoObjectId(rtId as string ?? ""));
                dp.DataRtId = rtEntityId;
            }

            dataPointDtos.Add(dp);
        }

        return dataPointDtos;
    }

    public async Task InsertDataAsync(string tenantId, DataPointDto datapoint)
    {
        var query = string.Format(Queries.InsertTimeSeriesEntry, tenantId);
        await using var connection = await CreateConnection();

        var data = new Json<Dictionary<string, object?>>(datapoint.Attributes.ToDictionary());

        var result = await connection.ExecuteAsync(query,
            new
            {
                datapoint.DataRtId.RtId,
                datapoint.DataRtId.CkTypeId,
                datapoint.Timestamp,
                data
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