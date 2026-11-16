using System.Text.Json;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Services.Methods.MongoDb;

/// <summary>
///     MongoDB implementation of <see cref="ICkMethodOperationStore" /> (AB#8443, OD-5): one document per operation in
///     the collection <see cref="CollectionName" /> of the <b>system database</b>
///     (<see cref="OctoSystemConfiguration.SystemDatabaseName" />), so every replica of every service sees the same
///     records and they survive a restart of the handler host.
///     <list type="bullet">
///         <item><description><b>Indexes</b> (created once per process): unique <c>tenantId + operationId</c>; TTL on <c>expiresAt</c> (<c>expireAfterSeconds: 0</c>; open records have no <c>expiresAt</c> and are never removed by it); <c>tenantId + acceptedAt</c> for the list; <c>state + heartbeatAt</c> for the sweep.</description></item>
///         <item><description><b>Expiry</b> is compared by every read as well: the TTL monitor runs about once a minute, a record past <c>expiresAt</c> is invisible before it is removed.</description></item>
///         <item><description><b>Final states win</b>: progress, heartbeat, start and completion are conditional updates on an open state; the sweep ends one record at a time, conditional on it still being open and stale.</description></item>
///         <item><description><b>Stored</b>: ids, method id, owner subject or client id, handler instance, state, progress, times, the error and the result JSON of a non-sensitive result. Never parameters or tokens.</description></item>
///         <item><description><b>Connection</b>: an own admin client with majority read and write concern (30 s <c>wtimeout</c>, OD-7) built from <see cref="OctoSystemConfiguration" />, outside any request transaction. Database problems are thrown as <see cref="CkMethodOperationStoreException" />.</description></item>
///     </list>
/// </summary>
public sealed class CkMethodMongoOperationStore : ICkMethodOperationStore, ICkMethodOperationTenantPurge, IDisposable
{
    /// <summary>Collection in the system database.</summary>
    public const string CollectionName = "MethodOperations";

    /// <summary>Name of the unique index on tenant and operation id.</summary>
    public const string UniqueIndexName = "tenantId_operationId_unique";

    /// <summary>Name of the TTL index on <c>expiresAt</c>.</summary>
    public const string TtlIndexName = "expiresAt_ttl";

    private const int DuplicateKeyErrorCode = 11000;
    private const int SweepBatchSize = 500;

    private readonly Lazy<MongoClient>? _client;
    private readonly Func<IMongoDatabase> _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CkMethodMongoOperationStore> _logger;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexed;

    /// <summary>Creates the store on the system database named by <paramref name="systemConfiguration" />.</summary>
    public CkMethodMongoOperationStore(IOptions<OctoSystemConfiguration> systemConfiguration, TimeProvider timeProvider,
        ILogger<CkMethodMongoOperationStore> logger)
    {
        var configuration = systemConfiguration.Value;
        _client = new Lazy<MongoClient>(() => CreateClient(configuration));
        var databaseName = SystemDatabaseName(configuration);
        _database = () => _client.Value.GetDatabase(databaseName);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Creates the store on a given database (tests).</summary>
    internal CkMethodMongoOperationStore(IMongoDatabase database, TimeProvider timeProvider,
        ILogger<CkMethodMongoOperationStore> logger)
    {
        _database = () => database;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.OperationId);
        var stored = record with
        {
            TenantId = CkMethodOperationTenant.Normalize(record.TenantId),
            State = CkMethodOperationState.Accepted
        };
        await Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            try
            {
                await collection.InsertOneAsync(ToDocument(stored), cancellationToken: ct).ConfigureAwait(false);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new CkMethodOperationStoreException("An operation with this id exists in the tenant.", ex);
            }
            catch (MongoCommandException ex) when (ex.Code == DuplicateKeyErrorCode)
            {
                throw new CkMethodOperationStoreException("An operation with this id exists in the tenant.", ex);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<long> DeleteTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var normalized = CkMethodOperationTenant.Normalize(tenantId);
        return Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var result = await collection
                .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("tenantId", normalized), ct)
                .ConfigureAwait(false);
            return result.IsAcknowledged ? result.DeletedCount : 0L;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> DeleteTenantAcceptedBeforeAsync(string tenantId, DateTime acceptedBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = CkMethodOperationTenant.Normalize(tenantId);
        return Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var result = await collection
                .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("tenantId", normalized) &
                                 Builders<BsonDocument>.Filter.Lt("acceptedAt", acceptedBeforeUtc), ct)
                .ConfigureAwait(false);
            return result.IsAcknowledged ? result.DeletedCount : 0L;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
        CancellationToken cancellationToken = default)
    {
        var tenant = CkMethodOperationTenant.Normalize(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var document = await collection.Find(Key(tenant, operationId) & NotExpired())
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return document is null ? null : FromDocument(document);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        var f = Builders<BsonDocument>.Filter;
        var filter = f.Eq("tenantId", CkMethodOperationTenant.Normalize(query.TenantId)) & NotExpired();
        if (query.Owner is { } owner)
        {
            filter &= owner.SubjectId is not null
                ? f.Eq("ownerSubjectId", owner.SubjectId)
                : f.Eq("ownerSubjectId", BsonNull.Value) & f.Eq("ownerClientId", owner.ClientId);
        }

        if (query.MethodId is not null)
        {
            filter &= f.Eq("methodId", query.MethodId);
        }

        if (query.State is { } state)
        {
            filter &= f.Eq("state", (int)state);
        }

        if (query.SinceUtc is { } since)
        {
            filter &= f.Gte("acceptedAt", since);
        }

        return Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var total = await collection.CountDocumentsAsync(filter, cancellationToken: ct).ConfigureAwait(false);
            var documents = await collection.Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Descending("acceptedAt").Ascending("operationId"))
                .Skip(query.Skip).Limit(query.Take)
                .ToListAsync(ct).ConfigureAwait(false);
            return new CkMethodOperationPage(documents.Select(FromDocument).ToList(), total);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(tenantId, operationId,
            Builders<BsonDocument>.Filter.Eq("state", (int)CkMethodOperationState.Accepted),
            Builders<BsonDocument>.Update.Set("state", (int)CkMethodOperationState.Running)
                .Set("startedAt", nowUtc).Set("heartbeatAt", nowUtc),
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
        DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Validate();
        return UpdateAsync(tenantId, operationId, Open(),
            Builders<BsonDocument>.Update.Set("progress", JsonSerializer.Serialize(progress))
                .Set("heartbeatAt", nowUtc),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(tenantId, operationId, Open(), Builders<BsonDocument>.Update.Set("heartbeatAt", nowUtc),
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        completion.Validate();
        return UpdateAsync(tenantId, operationId, Open(), Completion(completion), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
        CancellationToken cancellationToken = default) =>
        Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var stale = Open() & Builders<BsonDocument>.Filter.Lt("heartbeatAt", staleBeforeUtc);
            var candidates = await collection.Find(stale)
                .Project(Builders<BsonDocument>.Projection.Include("_id").Include("methodId"))
                .Limit(SweepBatchSize)
                .ToListAsync(ct).ConfigureAwait(false);
            var ended = 0;
            foreach (var candidate in candidates)
            {
                var methodId = candidate.GetValue("methodId", BsonString.Empty).AsString;
                var error = CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                    (CkMethodErrorParameters.MethodId, methodId),
                    (CkMethodErrorParameters.Reason, CkMethodOperationReasons.HandlerLost));
                var update = Completion(new CkMethodOperationCompletion
                {
                    State = CkMethodOperationState.Failed,
                    Error = error,
                    CompletedAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc + retention
                });
                // still open and still stale: a heartbeat or a completion in between wins
                var result = await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", candidate["_id"]) & stale, update,
                    cancellationToken: ct).ConfigureAwait(false);
                ended += (int)result.ModifiedCount;
            }

            return ended;
        }, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_client is { IsValueCreated: true })
        {
            _client.Value.Dispose();
        }

        _indexGate.Dispose();
    }

    private Task<bool> UpdateAsync(string tenantId, string operationId, FilterDefinition<BsonDocument> condition,
        UpdateDefinition<BsonDocument> update, CancellationToken cancellationToken)
    {
        var tenant = CkMethodOperationTenant.Normalize(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return Run(async ct =>
        {
            var collection = await GetCollectionAsync(ct).ConfigureAwait(false);
            var result = await collection.UpdateOneAsync(Key(tenant, operationId) & condition, update,
                cancellationToken: ct).ConfigureAwait(false);
            return result.MatchedCount == 1;
        }, cancellationToken);
    }

    private static UpdateDefinition<BsonDocument> Completion(CkMethodOperationCompletion completion) =>
        Builders<BsonDocument>.Update
            .Set("state", (int)completion.State)
            .Set("resultJson", completion.ResultJson is null ? BsonNull.Value : (BsonValue)completion.ResultJson)
            .Set("error", completion.Error is null ? BsonNull.Value : (BsonValue)JsonSerializer.Serialize(completion.Error))
            .Set("completedAt", completion.CompletedAtUtc)
            .Set("expiresAt", completion.ExpiresAtUtc);

    private static FilterDefinition<BsonDocument> Key(string tenantId, string operationId) =>
        Builders<BsonDocument>.Filter.Eq("tenantId", tenantId) &
        Builders<BsonDocument>.Filter.Eq("operationId", operationId);

    private static FilterDefinition<BsonDocument> Open() =>
        Builders<BsonDocument>.Filter.In("state",
            [(int)CkMethodOperationState.Accepted, (int)CkMethodOperationState.Running]);

    private FilterDefinition<BsonDocument> NotExpired() =>
        Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("expiresAt", BsonNull.Value),
            Builders<BsonDocument>.Filter.Gt("expiresAt", _timeProvider.GetUtcNow().UtcDateTime));

    internal static BsonDocument ToDocument(CkMethodOperationRecord record) => new()
    {
        { "tenantId", record.TenantId },
        { "operationId", record.OperationId },
        { "methodId", record.MethodId },
        { "invocationId", record.InvocationId.ToString("D") },
        { "correlationId", record.CorrelationId is { } c ? c.ToString("D") : BsonNull.Value },
        { "ownerSubjectId", record.Owner.SubjectId is { } s ? s : BsonNull.Value },
        { "ownerClientId", record.Owner.ClientId is { } c2 ? c2 : BsonNull.Value },
        { "handlerInstanceId", record.HandlerInstanceId },
        { "state", (int)record.State },
        { "progress", record.Progress is null ? BsonNull.Value : JsonSerializer.Serialize(record.Progress) },
        { "resultJson", record.ResultJson is null ? BsonNull.Value : record.ResultJson },
        { "error", record.Error is null ? BsonNull.Value : JsonSerializer.Serialize(record.Error) },
        { "acceptedAt", record.AcceptedAtUtc },
        { "startedAt", record.StartedAtUtc is { } st ? st : BsonNull.Value },
        { "heartbeatAt", record.HeartbeatAtUtc },
        { "completedAt", record.CompletedAtUtc is { } co ? co : BsonNull.Value },
        { "expiresAt", record.ExpiresAtUtc is { } ex ? ex : BsonNull.Value }
    };

    private static CkMethodOperationRecord FromDocument(BsonDocument d) => new()
    {
        TenantId = d["tenantId"].AsString,
        OperationId = d["operationId"].AsString,
        MethodId = d["methodId"].AsString,
        InvocationId = Guid.Parse(d["invocationId"].AsString),
        CorrelationId = String(d, "correlationId") is { } c ? Guid.Parse(c) : null,
        Owner = new CkMethodOperationOwner(String(d, "ownerSubjectId"), String(d, "ownerClientId")),
        HandlerInstanceId = d["handlerInstanceId"].AsString,
        State = (CkMethodOperationState)d["state"].ToInt32(),
        Progress = String(d, "progress") is { } p ? JsonSerializer.Deserialize<CkMethodOperationProgress>(p) : null,
        ResultJson = String(d, "resultJson"),
        Error = String(d, "error") is { } e ? JsonSerializer.Deserialize<CkMethodError>(e) : null,
        AcceptedAtUtc = d["acceptedAt"].ToUniversalTime(),
        StartedAtUtc = Date(d, "startedAt"),
        HeartbeatAtUtc = d["heartbeatAt"].ToUniversalTime(),
        CompletedAtUtc = Date(d, "completedAt"),
        ExpiresAtUtc = Date(d, "expiresAt")
    };

    private static string? String(BsonDocument d, string name) =>
        d.TryGetValue(name, out var v) && !v.IsBsonNull ? v.AsString : null;

    private static DateTime? Date(BsonDocument d, string name) =>
        d.TryGetValue(name, out var v) && !v.IsBsonNull ? v.ToUniversalTime() : null;

    // Every database problem is a store failure (fail closed); the caller's own cancellation passes through.
    private static async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not CkMethodOperationStoreException && ex is not ArgumentException &&
                                   !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new CkMethodOperationStoreException("The operation store failed: " + ex.GetType().Name, ex);
        }
    }

    private async Task<IMongoCollection<BsonDocument>> GetCollectionAsync(CancellationToken cancellationToken)
    {
        var collection = _database().GetCollection<BsonDocument>(CollectionName);
        if (_indexed)
        {
            return collection;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_indexed)
            {
                var keys = Builders<BsonDocument>.IndexKeys;
                await collection.Indexes.CreateManyAsync(
                [
                    new CreateIndexModel<BsonDocument>(keys.Ascending("tenantId").Ascending("operationId"),
                        new CreateIndexOptions { Unique = true, Name = UniqueIndexName }),
                    new CreateIndexModel<BsonDocument>(keys.Ascending("expiresAt"),
                        new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = TtlIndexName }),
                    new CreateIndexModel<BsonDocument>(keys.Ascending("tenantId").Descending("acceptedAt"),
                        new CreateIndexOptions { Name = "tenantId_acceptedAt" }),
                    new CreateIndexModel<BsonDocument>(keys.Ascending("state").Ascending("heartbeatAt"),
                        new CreateIndexOptions { Name = "state_heartbeatAt" })
                ], cancellationToken).ConfigureAwait(false);
                _indexed = true;
                _logger.LogDebug("Indexes of {Collection} ensured", CollectionName);
            }
        }
        finally
        {
            _indexGate.Release();
        }

        return collection;
    }

    /// <summary>
    ///     AB#8444: the system database as the engine names it, trimmed and lower-case (<c>SystemContext</c> normalizes
    ///     <see cref="OctoSystemConfiguration.SystemDatabaseName" /> the same way). MongoDB refuses a database whose name
    ///     differs from an existing one only in case, so the configured spelling (default <c>OctoSystem</c>) must not be
    ///     used as is: the first write failed with "db already exists with different case".
    /// </summary>
    internal static string SystemDatabaseName(OctoSystemConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.SystemDatabaseName);
        return configuration.SystemDatabaseName.Trim().ToLowerInvariant();
    }

    private static MongoClient CreateClient(OctoSystemConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.AdminUser) ||
            string.IsNullOrWhiteSpace(configuration.AdminUserPassword))
        {
            throw new CkMethodOperationStoreException(
                "The operation store needs the MongoDB admin credentials (System:AdminUser / AdminUserPassword).");
        }

        var urlBuilder = new MongoUrlBuilder();
        // Parse, not the string ctor: DatabaseHost may be "host:port" (same as the engine).
        if (configuration.DatabaseHost.Contains(','))
        {
            urlBuilder.Servers = configuration.DatabaseHost.Split(',').Select(MongoServerAddress.Parse);
        }
        else
        {
            urlBuilder.Server = MongoServerAddress.Parse(configuration.DatabaseHost);
        }

        urlBuilder.Username = configuration.AdminUser;
        urlBuilder.Password = configuration.AdminUserPassword;
        urlBuilder.AuthenticationSource = configuration.AuthenticationDatabaseName;
        urlBuilder.ApplicationName = "octo-method-operations";
        urlBuilder.UseTls = configuration.UseTls;
        urlBuilder.AllowInsecureTls = configuration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = configuration.UseDirectConnection;
        if (!string.IsNullOrWhiteSpace(configuration.ReplicaSetName))
        {
            urlBuilder.ReplicaSetName = configuration.ReplicaSetName;
        }

        var settings = MongoClientSettings.FromUrl(urlBuilder.ToMongoUrl());
        settings.ReadConcern = ReadConcern.Majority;
        settings.WriteConcern = new WriteConcern(WriteConcern.WMode.Majority, TimeSpan.FromSeconds(30));
        return new MongoClient(settings);
    }
}
