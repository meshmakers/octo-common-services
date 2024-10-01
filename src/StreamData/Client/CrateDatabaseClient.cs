using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.StreamData.Dapper;
using Meshmakers.Octo.Services.Common.StreamData.Dtos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Meshmakers.Octo.Services.Common.StreamData.Client;

/// <summary>
/// Client for interacting with the stream data database.
/// </summary>
internal class CrateDatabaseClient : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient,
    IStreamDataHealthCheckClient
{
    private readonly ILogger<CrateDatabaseClient> _logger;
    private readonly ICrateDbConnectionAccess _connectionAccess;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="connectionAccess"></param>
    /// <param name="logger"></param>
    public CrateDatabaseClient(ILogger<CrateDatabaseClient> logger, ICrateDbConnectionAccess connectionAccess)
    {
        _logger = logger;
        _connectionAccess = connectionAccess;

        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, object>>());
        SqlMapper.AddTypeHandler(new CkIdTypeHandler());
        SqlMapper.AddTypeHandler(new OctoIdTypeHandler());
    }

    public async Task<List<DataPointDto>> GetDataAsync(string tenantId, string query)
    {
        await using var connection = CreateConnection(tenantId);

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
            else if (result.TryGetValue("T", out var ts))
            {
                dp.Timestamp = (DateTime)ts!;
            }

            if (result.TryGetValue(Constants.RtId, out var rtIdValue) &&
                OctoObjectId.TryParse(rtIdValue as string ?? "", out var octoRtId))
            {
                dp.RtId = octoRtId;
            }

            if (result.TryGetValue(Constants.CkTypeId, out var ckTypeIdValue))
            {
                var typeId = new CkId<CkTypeId>(ckTypeIdValue as string ?? "");
                dp.CkTypeId = typeId;
            }

            dataPointDtos.Add(dp);
        }

        return dataPointDtos;
    }

    public async Task InsertDataAsync(string tenantId, IEnumerable<DataPointDto> datapoints)
    {
        await using var connection = CreateConnection(tenantId);

        var d = datapoints.ToArray();

        var command = new NpgsqlCommand(string.Format(Queries.InsertStreamDataBulk, tenantId), connection);


        var dataParameter = new NpgsqlParameter("@data", NpgsqlDbType.Array | NpgsqlDbType.Json)
        {
            Value = d.Select(x => x.Attributes).ToArray()
        };

        command.Parameters.Add(new NpgsqlParameter<string[]>("@rtIds",
            d.Select(x => x.RtId.ToString()!).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("@ckTypeIds",
            d.Select(x => x.CkTypeId!.ToString()).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("@timestamps",
            d.Select(x => x.Timestamp).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("@rtWellKnownNames",
            d.Select(x => x.RtWellKnownName).ToArray()));
        command.Parameters.Add(dataParameter);

        command.ExecuteNonQuery();
    }

    public async Task InsertDataAsync(string tenantId, DataPointDto datapoint)
    {
        var query = string.Format(Queries.InsertStreamDataEntry, tenantId);
        await using var connection = CreateConnection(tenantId);

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
        await using var connection = CreateConnection(tenantId);

        var result = await connection.ExecuteAsync(string.Format(Queries.CreateTableIfNotExists, tenantId));
    }

    public async Task DeleteStreamDataDatabaseAsync(string tenantId)
    {
        await using var connection = CreateConnection(tenantId);

        var result = await connection.ExecuteAsync(string.Format(Queries.DeleteTableIfExists, tenantId));
    }

    private NpgsqlConnection CreateConnection(string tenantId)
    {
        return _connectionAccess.CreateConnection(tenantId);
    }

    public async Task<HealthCheckResult> CheckHealthAsync()
    {
        try
        {
            await using var connection = CreateConnection("default");
            await connection.ExecuteAsync("SELECT 1");
            return HealthCheckResult.Healthy("CrateDB is healthy");
        }
        catch (Exception ex)
        {
            _logger.LogError("CrateDB is unhealthy: {Message}", ex.Message);
            return HealthCheckResult.Unhealthy("CrateDB is unhealthy");
        }
    }
}