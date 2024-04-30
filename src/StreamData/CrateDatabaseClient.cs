using System.Diagnostics;
using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.StreamData.Configuration;
using Meshmakers.Octo.Services.Common.StreamData.Dapper;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Services.Common.StreamData;

/// <summary>
/// Client for interacting with the stream data database.
/// </summary>
internal class CrateDatabaseClient : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient
{
    private readonly string _connectionString;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="options"></param>
    public CrateDatabaseClient(IOptions<StreamDataConfiguration> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.Value.ConnectionString);

        _connectionString = options.Value.ConnectionString;

        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, object>>());
        SqlMapper.AddTypeHandler(new CkIdTypeHandler());
        SqlMapper.AddTypeHandler(new OctoIdTypeHandler());
    }

    public async Task<List<DataPointDto>> GetDataAsync(string query)
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

            if (result.TryGetValue(Constants.RtId, out var rtIdValue) &&
                OctoObjectId.TryParse(rtIdValue as string ?? "", out var octoRtId))
            {
                dp.RtId = octoRtId;
            }
            if(result.TryGetValue(Constants.CkTypeId, out var ckTypeIdValue))
            {
                var typeId = new CkId<CkTypeId>(ckTypeIdValue as string ?? "");
                dp.CkTypeId = typeId;
            }
            
            dataPointDtos.Add(dp);
        }

        return dataPointDtos;
    }

    public async Task InsertDataAsync(string tenantId, DataPointDto datapoint)
    {
        var query = string.Format(Queries.InsertStreamDataEntry, tenantId);
        await using var connection = await CreateConnection();

        var data = new Json<Dictionary<string, object?>>(datapoint.Attributes.ToDictionary());

        var result = await connection.ExecuteAsync(query,
            new
            {
                datapoint.RtId,
                datapoint.CkTypeId,
                datapoint.Timestamp,
                data
            });
    }

    public async Task CreateStreamDataTableIfNotExistAsync(string tenantId)
    {
        await using var connection = await CreateConnection();
        var result = await connection.ExecuteAsync(string.Format(Queries.CreateTableIfNotExists, tenantId));
    }

    public async Task DeleteStreamDataDatabaseAsync(string tenantId)
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