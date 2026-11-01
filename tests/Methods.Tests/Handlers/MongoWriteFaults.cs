using System.Net;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>
///     The exceptions the MongoDB driver throws when a write is not acknowledged in time (AB#8020), built without a server.
///     <c>WriteConcernError</c> and <c>WriteError</c> have no public constructor, so they are created by reflection.
/// </summary>
internal static class MongoWriteFaults
{
    /// <summary>MongoDB error code <c>WriteConcernTimeout</c> (the <c>wtimeout</c> expired).</summary>
    public const int WriteConcernTimeout = 64;

    private static ConnectionId Connection { get; } =
        new(new ServerId(new ClusterId(), new DnsEndPoint("octo-mongodb-0", 27017)));

    /// <summary>A command or commit whose write-concern wait timed out (<c>findAndModify</c>, <c>commitTransaction</c>).</summary>
    public static MongoWriteConcernException WriteConcernTimeoutOnCommand() =>
        new(Connection, "Command failed with a write concern error: waiting for replication timed out",
            new WriteConcernResult(new BsonDocument
            {
                { "ok", 1 },
                { "n", 1 },
                {
                    "writeConcernError",
                    new BsonDocument { { "code", WriteConcernTimeout }, { "codeName", "WriteConcernTimeout" }, { "errmsg", "waiting for replication timed out" } }
                }
            }));

    /// <summary>A single-document write (insert/update/replace/delete) whose write-concern wait timed out.</summary>
    public static MongoWriteException WriteConcernTimeoutOnSingleWrite() =>
        new(Connection, null, TimeoutError(), null);

    /// <summary>A bulk write whose write-concern wait timed out (all writes applied on the primary).</summary>
    public static MongoBulkWriteException<BsonDocument> WriteConcernTimeoutOnBulkWrite()
    {
        WriteModel<BsonDocument>[] processed = [new InsertOneModel<BsonDocument>(new BsonDocument("_id", 1))];
        var result = new BulkWriteResult<BsonDocument>.Acknowledged(1, 0, 0, 1, 0, processed, []);
        return new MongoBulkWriteException<BsonDocument>(Connection, result, [], TimeoutError(), []);
    }

    /// <summary>A write the server refused (duplicate key) - nothing was written, no write-concern error.</summary>
    public static MongoWriteException DuplicateKey() =>
        new(Connection, Create<WriteError>(ServerErrorCategory.DuplicateKey, 11000, "E11000 duplicate key error",
            new BsonDocument()), null, null);

    /// <summary>A network error during a transaction commit: the driver labels it <c>UnknownTransactionCommitResult</c>.</summary>
    public static MongoConnectionException CommitNetworkErrorWithUnknownResult()
    {
        var exception = new MongoConnectionException(Connection, "connection reset during commitTransaction");
        exception.AddErrorLabel("UnknownTransactionCommitResult");
        return exception;
    }

    /// <summary>A command error that has nothing to do with the write concern (code 2, BadValue).</summary>
    public static MongoCommandException BadValue() =>
        new(Connection, "BadValue", new BsonDocument("ping", 1),
            new BsonDocument { { "ok", 0 }, { "code", 2 }, { "codeName", "BadValue" }, { "errmsg", "bad value" } });

    private static WriteConcernError TimeoutError() =>
        Create<WriteConcernError>(WriteConcernTimeout, "WriteConcernTimeout", "waiting for replication timed out",
            new BsonDocument(), Array.Empty<string>());

    private static T Create<T>(params object?[] arguments)
    {
        var constructor = typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == arguments.Length);
        return (T)constructor.Invoke(arguments);
    }
}
