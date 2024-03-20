using System.Text;

namespace Meshmakers.Octo.Services.Common.Timeseries;

internal static class Queries
{
    public const string GetAllTables = """
                                       SELECT schemaname, tablename
                                       FROM pg_catalog.pg_tables
                                       WHERE schemaname != 'pg_catalog' AND schemaname != 'information_schema' limit 100;
                                       """;

    public const string GetAllTablesExceptSys = """
                                                SELECT schemaname, tablename
                                                FROM pg_catalog.pg_tables
                                                WHERE schemaname != 'pg_catalog' AND schemaname != 'information_schema' AND schemaname != 'sys' limit 100;
                                                """;

    public const string CreateTableIfNotExists = """
                                                 create table if not exists {0}(
                                                 ckid TEXT not null,
                                                 rtid TEXT not null,
                                                 "timestamp" TIMESTAMP WITH TIME ZONE not null,
                                                 data object(Dynamic) not null,
                                                 "minute" GENERATED always as date_trunc('hour', "timestamp")
                                                 ) clustered into 3 shards;
                                                 """;
    
    public const string DeleteTableIfExists = "drop table if exists {0};";

    public const string InsertTimeSeriesEntry =
        """insert into {0} (rtid, ckid, "timestamp", data) values (@rtid, @ckid, @timestamp, @data);""";


    public const string SelectTimeSeriesDataByRtIdAndTimestamp = """
                                                                 SELECT "timestamp", data
                                                                 FROM {0}
                                                                 WHERE rtid = @rtId AND "timestamp" >= @from and "timestamp" <= @to
                                                                 ORDER BY "timestamp"
                                                                 LIMIT @limit
                                                                 OFFSET @offset;
                                                                 """;


    public const string SelectStatisticalValuesByMonth = """
                                                         SELECT
                                                            rtId,
                                                            data['Designation'] as designation,
                                                            date_trunc('month', "timestamp") AS month,
                                                            AVG(data['ProductionPower']) AS avg_production_power,
                                                            MIN(data['ProductionPower']) AS min_production_power,
                                                            MAX(data['ProductionPower']) AS max_production_power,
                                                            VARIANCE(data['ProductionPower']) AS var_production_power,
                                                            STDDEV(data['ProductionPower']) AS stddev_production_power
                                                         FROM meshtest.meshmakers_assets_photovoltaic_pvmodules
                                                         GROUP BY month, rtId, designation
                                                         ORDER BY month
                                                         LIMIT 100;
                                                         """;
    
    public static string CreateInsertTimeSeriesEntriesQuery(string tableName, int numberOfEntries)
    {
        StringBuilder query = new StringBuilder($"insert into {tableName} (rtid, \"timestamp\", data) values ");

        for (int i = 0; i < numberOfEntries; i++)
        {
            query.Append($"(@rtid{i}, @timestamp{i}, @data{i})");
            if (i < numberOfEntries - 1)
            {
                query.Append(", ");
            }
        }

        query.Append(';');

        return query.ToString();
    }
}