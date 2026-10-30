using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Services.Methods.Measurements;

/// <summary>
///     What a claim write costs (AB#7823, baseline for AB#7824 and the decision record of OD-3 option A / B'): the unique
///     insert that takes an idempotency claim and the conditional update that completes it, on a single-node MongoDB
///     replica set in a private container, majority write concern. The document and the filters mirror
///     <c>MongoMethodIdempotencyStore</c> of octo-asset-repo-services (AB#7824): <c>_id</c> is the key hash, state
///     0 = in progress, 1 = completed, completion is conditional on id, first invocation, state and lease.
/// </summary>
[Collection(MeasurementCollection.Name)]
[Trait("Category", "Measurement")]
public sealed class ClaimWriteBenchmarkTests(MeasurementFixture fixture, ITestOutputHelper output)
{
    private const int WarmUp = 500;
    private const int Samples = 5000;
    private const string Database = "measurement";

    private static readonly string Response = new('r', 700); // an envelope of a typical result, a few hundred bytes

    private static BsonDocument ClaimDocument(string id, Guid invocationId, DateTime now) => new()
    {
        { "_id", id },
        { "state", 0 },
        { "methodId", "System.Identity/User.ChangeEmail-1" },
        { "firstInvocationId", new BsonBinaryData(invocationId, GuidRepresentation.Standard) },
        { "fingerprint", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) },
        { "fingerprintKeyId", "current" },
        { "claimedAt", now },
        { "leaseUntil", now.AddSeconds(30) },
        { "expiresAt", now.AddHours(24) },
        { "response", BsonNull.Value }
    };

    private static string KeyId(int n) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"tenant|method|subject|{Guid.NewGuid():N}|{n}")));

    private static async Task<IMongoCollection<BsonDocument>> NewCollectionAsync(IMongoClient client)
    {
        var name = $"MethodIdempotency_{Guid.NewGuid():N}";
        var collection = client.GetDatabase(Database).GetCollection<BsonDocument>(name)
            .WithWriteConcern(WriteConcern.WMajority);
        // the TTL index of the real store (housekeeping only)
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
            new CreateIndexOptions { Name = "expiresAt_ttl", ExpireAfter = TimeSpan.Zero }));
        return collection;
    }

    private static async Task<(IReadOnlyList<double> Claim, IReadOnlyList<double> Complete, IReadOnlyList<double> Both)>
        RunSequentialAsync(IMongoCollection<BsonDocument> collection, int warmUp, int samples)
    {
        var claim = new List<double>(samples);
        var complete = new List<double>(samples);
        var both = new List<double>(samples);
        for (var i = 0; i < warmUp + samples; i++)
        {
            var id = KeyId(i);
            var invocation = Guid.NewGuid();
            var now = DateTime.UtcNow;

            var t0 = Stopwatch.GetTimestamp();
            await collection.InsertOneAsync(ClaimDocument(id, invocation, now));
            var t1 = Stopwatch.GetTimestamp();
            var update = await collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", id),
                    Builders<BsonDocument>.Filter.Eq("firstInvocationId", new BsonBinaryData(invocation, GuidRepresentation.Standard)),
                    Builders<BsonDocument>.Filter.Eq("state", 0),
                    Builders<BsonDocument>.Filter.Gt("expiresAt", DateTime.UtcNow)),
                Builders<BsonDocument>.Update.Set("state", 1).Set("response", Response));
            var t2 = Stopwatch.GetTimestamp();
            Assert.Equal(1, update.ModifiedCount);

            if (i >= warmUp)
            {
                claim.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                complete.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
                both.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
            }
        }

        return (claim, complete, both);
    }

    [Fact]
    public async Task ClaimInsert_AndConditionalComplete_Sequential_Latency()
    {
        Assert.SkipUnless(MeasurementFixture.Enabled, MeasurementFixture.SkipReason);
        var client = new MongoClient(fixture.MongoConnectionString);
        var collection = await NewCollectionAsync(client);

        var (claim, complete, both) = await RunSequentialAsync(collection, WarmUp, Samples);

        output.WriteLine($"MEASURE claim insert (unique _id, w:majority), sequential: {Statistics.Summarize(claim)}");
        output.WriteLine($"MEASURE conditional update (complete), sequential:        {Statistics.Summarize(complete)}");
        output.WriteLine($"MEASURE claim + complete per call, sequential:           {Statistics.Summarize(both)}");
    }

    [Fact]
    public async Task DuplicateClaim_UniqueKeyViolation_Latency()
    {
        Assert.SkipUnless(MeasurementFixture.Enabled, MeasurementFixture.SkipReason);
        var client = new MongoClient(fixture.MongoConnectionString);
        var collection = await NewCollectionAsync(client);
        var ids = Enumerable.Range(0, 1000).Select(KeyId).ToArray();
        foreach (var id in ids)
        {
            await collection.InsertOneAsync(ClaimDocument(id, Guid.NewGuid(), DateTime.UtcNow));
        }

        var losers = new List<double>(ids.Length);
        foreach (var id in ids)
        {
            var t0 = Stopwatch.GetTimestamp();
            var e = await Assert.ThrowsAsync<MongoWriteException>(async () =>
                await collection.InsertOneAsync(ClaimDocument(id, Guid.NewGuid(), DateTime.UtcNow)));
            losers.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        }

        output.WriteLine($"MEASURE losing claim (duplicate key), sequential:        {Statistics.Summarize(losers)}");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public async Task ClaimInsert_AndConditionalComplete_Concurrent_Latency(int concurrency)
    {
        Assert.SkipUnless(MeasurementFixture.Enabled, MeasurementFixture.SkipReason);
        var client = new MongoClient(fixture.MongoConnectionString);
        var collection = await NewCollectionAsync(client);

        // the concurrency of several asset-repo replicas serving keyed calls at once
        var claim = new double[Samples];
        var complete = new double[Samples];
        var both = new double[Samples];
        var wall = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, Samples),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, _) =>
            {
                var id = KeyId(i);
                var invocation = Guid.NewGuid();
                var t0 = Stopwatch.GetTimestamp();
                await collection.InsertOneAsync(ClaimDocument(id, invocation, DateTime.UtcNow));
                var t1 = Stopwatch.GetTimestamp();
                await collection.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("_id", id),
                        Builders<BsonDocument>.Filter.Eq("firstInvocationId", new BsonBinaryData(invocation, GuidRepresentation.Standard)),
                        Builders<BsonDocument>.Filter.Eq("state", 0)),
                    Builders<BsonDocument>.Update.Set("state", 1).Set("response", Response));
                var t2 = Stopwatch.GetTimestamp();
                claim[i] = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                complete[i] = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                both[i] = Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds;
            });
        wall.Stop();

        output.WriteLine($"MEASURE concurrency {concurrency} claim insert:   {Statistics.Summarize(claim)}");
        output.WriteLine($"MEASURE concurrency {concurrency} complete:       {Statistics.Summarize(complete)}");
        output.WriteLine($"MEASURE concurrency {concurrency} claim+complete: {Statistics.Summarize(both)}; throughput " +
                         $"{(Samples / wall.Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture)} claim+complete/s");
    }
}
