
namespace Meshmakers.Octo.Services.Common.StreamData;

internal static class Queries
{
    public const string CreateTableIfNotExists = """
                                                 create table if not exists {0}(
                                                 "CkTypeId" TEXT not null,
                                                 "RtId" TEXT not null,
                                                 "Timestamp" TIMESTAMP WITH TIME ZONE not null,
                                                 data object(Dynamic),
                                                 PRIMARY KEY ("Timestamp", "RtId", "CkTypeId")
                                                 ) clustered into 3 shards;
                                                 """;
    
    public const string DeleteTableIfExists = "drop table if exists {0};";

    public const string InsertStreamDataEntry =
        """
        insert into {0} ("RtId", "CkTypeId", "Timestamp", data)
        values (@RtId, @CkTypeId, @Timestamp, @data)
        ON CONFLICT ("Timestamp", "RtId", "CkTypeId")
        DO UPDATE SET "data" = "data" || EXCLUDED."data";
        """;

    public const string InsertStreamDataBulk =
        """
        INSERT INTO {0} ("RtId", "CkTypeId", "Timestamp", "data")
        SELECT * FROM unnest(@rtIds, @ckTypeIds, @timestamps, @data)
        ON CONFLICT ("Timestamp", "RtId", "CkTypeId")
        DO UPDATE SET "data" = "data" || EXCLUDED."data";
        """;
}