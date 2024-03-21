
namespace Meshmakers.Octo.Services.Common.Timeseries;

internal static class Queries
{
    public const string CreateTableIfNotExists = """
                                                 create table if not exists {0}(
                                                 "CkTypeId" TEXT not null,
                                                 "RtId" TEXT not null,
                                                 "Timestamp" TIMESTAMP WITH TIME ZONE not null,
                                                 data object(Dynamic)
                                                 ) clustered into 3 shards;
                                                 """;
    
    public const string DeleteTableIfExists = "drop table if exists {0};";

    public const string InsertTimeSeriesEntry =
        """insert into {0} ("RtId", "CkTypeId", "Timestamp", data) values (@RtId, @CkTypeId, @Timestamp, @data);""";
}