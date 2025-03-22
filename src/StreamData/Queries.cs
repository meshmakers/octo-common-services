
namespace Meshmakers.Octo.StreamData;

internal static class Queries
{
    public const string CreateTableIfNotExists = """
                                                 CREATE TABLE IF NOT EXISTS {0} (
                                                   "CkTypeId" TEXT NOT NULL,
                                                   "RtId" TEXT NOT NULL,
                                                   "Timestamp" TIMESTAMP WITH TIME ZONE NOT NULL,
                                                   "RtCreationDateTime" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                                                   "RtChangedDateTime" TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                                                   "RtWellKnownName" TEXT,
                                                   "data" OBJECT(DYNAMIC),
                                                   PRIMARY KEY ("Timestamp", "RtId", "CkTypeId")
                                                 ) CLUSTERED INTO 3 SHARDS;
                                                 """;
    
    public const string DeleteTableIfExists = "drop table if exists {0};";

    public const string InsertStreamDataEntry =
        """
        INSERT INTO {0} ("RtId", "CkTypeId", "Timestamp", "RtWellKnownName", data)
        VALUES (@RtId, @CkTypeId, @Timestamp, @WellKnownName, @data)
        ON CONFLICT ("Timestamp", "RtId", "CkTypeId")
        DO UPDATE SET
            "data" = "data" || EXCLUDED."data",
            "RtChangedDateTime" = CURRENT_TIMESTAMP,
            "RtCreationDateTime" = "RtCreationDateTime"
        """;

    public const string InsertStreamDataBulk =
        """
        INSERT INTO {0} ("RtId", "CkTypeId", "Timestamp", "RtWellKnownName", "data")
        SELECT * FROM unnest(@rtIds, @ckTypeIds, @timestamps, @rtWellKnownNames, @data)
        ON CONFLICT ("Timestamp", "RtId", "CkTypeId")
        DO UPDATE SET
            "data" = "data" || EXCLUDED."data",
            "RtChangedDateTime" = CURRENT_TIMESTAMP,
            "RtCreationDateTime" = "RtCreationDateTime"
        """;
}