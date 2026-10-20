using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;

namespace ArtifactStorage.Tests;

/// <summary>
///     CrateDB and an S3 store (MinIO) on one Docker network: the store is reachable for the test through a host port and
///     for CrateDB as <c>minio:9000</c> — the two endpoints of the staging location (AB#6969). The MinIO secret contains
///     the characters that must survive the URI.
/// </summary>
public class CrateDbMinioFixture : ContainerFixtureBase
{
    public const string DefaultCrateImage = "crate:6.4.6";
    public const string Bucket = "octo-artifacts-test";
    public const string AccessKey = "octoaccess";

    // Fake credential for the throw-away container: '/', '+' and '@' are what a real secret key can contain.
    public const string SecretKey = "oct0/sec+ret@key";

    /// <summary>
    ///     The site region configured on the MinIO server, or <c>null</c> for none. Only a configured region makes MinIO send
    ///     <c>x-amz-bucket-region</c> with its answers; without one the header is absent, which is why the default store never
    ///     exercised the region handling of CrateDB (AB#7059).
    /// </summary>
    protected virtual string? StoreRegion => null;

    private INetwork? _network;
    private MinioContainer? _minio;
    private IContainer? _crate;

    public S3ArtifactStorageOptions Options { get; private set; } = new();

    public string CrateSqlUrl { get; private set; } = string.Empty;

    protected override async Task StartAsync(CancellationToken cancellationToken)
    {
        var minioImage = Environment.GetEnvironmentVariable("OCTO_TEST_MINIO_IMAGE");
        var crateImage = Environment.GetEnvironmentVariable("OCTO_TEST_CRATEDB_IMAGE");
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(cancellationToken);

        var minioBuilder = new MinioBuilder(string.IsNullOrWhiteSpace(minioImage) ? MinioFixture.DefaultImage : minioImage)
            .WithNetwork(_network)
            .WithNetworkAliases("minio")
            .WithUsername(AccessKey)
            .WithPassword(SecretKey);
        if (StoreRegion != null)
        {
            // MINIO_REGION is the name of older images, MINIO_SITE_REGION of newer ones.
            minioBuilder = minioBuilder.WithEnvironment("MINIO_REGION", StoreRegion).WithEnvironment("MINIO_SITE_REGION", StoreRegion);
        }

        _minio = minioBuilder.Build();
        await _minio.StartAsync(cancellationToken);

        _crate = new ContainerBuilder(string.IsNullOrWhiteSpace(crateImage) ? DefaultCrateImage : crateImage)
            .WithNetwork(_network)
            .WithNetworkAliases("crate")
            .WithPortBinding(4200, true)
            .WithCommand("crate", "-Cdiscovery.type=single-node", "-Cnetwork.host=_site_")
            .WithEnvironment("CRATE_HEAP_SIZE", "512m")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4200).ForPath("/")))
            .Build();
        await _crate.StartAsync(cancellationToken);
        CrateSqlUrl = $"http://{_crate.Hostname}:{_crate.GetMappedPublicPort(4200)}/_sql";

        Options = new S3ArtifactStorageOptions
        {
            ServiceUrl = _minio.GetConnectionString(),
            CrateDbServiceUrl = "http://minio:9000",
            Region = StoreRegion ?? "us-east-1",
            Bucket = Bucket,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            ForcePathStyle = true
        };
        using var client = S3ArtifactStore.CreateClient(Options);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_crate != null)
        {
            await _crate.DisposeAsync();
        }

        if (_minio != null)
        {
            await _minio.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DisposeAsync();
        }
    }
}

/// <summary>
///     The same pair as <see cref="CrateDbMinioFixture" />, but the store runs in the region <c>at-vie-1</c>, as Exoscale SOS
///     does: it names that region in the <c>x-amz-bucket-region</c> header of its answers (AB#7059).
/// </summary>
public sealed class CrateDbMinioRegionFixture : CrateDbMinioFixture
{
    public const string Region = "at-vie-1";

    protected override string? StoreRegion => Region;
}

/// <summary>
///     A staging location created by the factory is accepted by CrateDB for <c>COPY TO</c> and <c>COPY FROM</c>, and
///     the files CrateDB writes are the objects the artifact store lists below the run prefix.
/// </summary>
[Trait("Category", "Container")]
public sealed class ArchiveCopyStagingCrateDbTests(CrateDbMinioFixture fixture, CrateDbMinioRegionFixture regionFixture)
    : IClassFixture<CrateDbMinioFixture>, IClassFixture<CrateDbMinioRegionFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task CopyToThenCopyFrom_RoundTripsThroughTheStagingLocation() => RoundTrip(fixture);

    /// <summary>
    ///     AB#7059: a store that sends <c>x-amz-bucket-region</c> with a region other than <c>us-east-1</c> (Exoscale SOS in
    ///     <c>at-vie-1</c>) must work for <c>COPY TO</c> and <c>COPY FROM</c>. The AWS client of CrateDB 5.x followed that header
    ///     and then resolved the region from the host name, which failed for such a store; 6.x talks to it as it is.
    /// </summary>
    [Fact]
    public Task CopyToThenCopyFrom_StoreInAnotherRegion_RoundTripsThroughTheStagingLocation() => RoundTrip(regionFixture);

    private async Task RoundTrip(CrateDbMinioFixture fixture)
    {
        fixture.SkipIfUnavailable();
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            InstancePrefix = "main",
            S3 = fixture.Options
        };
        var keyBuilder = new ArtifactKeyBuilder(options.InstancePrefix);
        var factory = new ArchiveCopyStagingLocationFactory(Options.Create(options), keyBuilder);
        using var store = new S3ArtifactStore(fixture.Options);
        var runId = $"run{Guid.NewGuid():N}"[..16];
        using var http = new HttpClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"doc.copy_src_{suffix}";
        var target = $"doc.copy_dst_{suffix}";

        await Sql(fixture, http, $"CREATE TABLE {source} (id INTEGER, label TEXT, ts TIMESTAMP WITH TIME ZONE) WITH (number_of_replicas = 0)", null);
        await Sql(fixture, http, $"CREATE TABLE {target} (id INTEGER, label TEXT, ts TIMESTAMP WITH TIME ZONE) WITH (number_of_replicas = 0)", null);
        await Sql(fixture, http, $"INSERT INTO {source} (id, label, ts) SELECT x, 'row-' || x, 1760000000000 + x FROM generate_series(1, 500) AS g(x)", null);
        await Sql(fixture, http, $"REFRESH TABLE {source}", null);

        var dump = factory.Create("Sbeg", runId, ArchiveCopyStagingAccess.Dump, "rt1/data/slice-0001");
        var copyTo = await Sql(fixture, http, $"COPY {source} TO DIRECTORY '{dump.RevealUri()}' WITH ({WithClause(dump, "compression = 'gzip'")})", dump);

        // CrateDB wrote the files; the artifact store sees them as nested keys below the run prefix.
        var keys = new List<string>();
        await foreach (var info in store.ListAsync(keyBuilder.RunPrefix(ArtifactCategories.CopyStaging, "sbeg", runId), Ct))
        {
            keys.Add(info.Key);
        }

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(keyBuilder.TryParseNested(key, out var parts), key);
            Assert.StartsWith("rt1/data/slice-0001/", parts!.RelativePath);
            Assert.EndsWith(".json.gz", key);
        });

        var restore = factory.Create("sbeg", runId, ArchiveCopyStagingAccess.Restore, "rt1/data/slice-0001");
        var copyFrom = await Sql(fixture, http, $"COPY {target} FROM '{restore.RevealUri()}/*' WITH ({WithClause(restore, "compression = 'gzip'")}) RETURN SUMMARY", restore);
        await Sql(fixture, http, $"REFRESH TABLE {target}", null);

        var count = await Sql(fixture, http, $"SELECT count(*), sum(id) FROM {target}", null);
        Assert.Equal(500, count.GetProperty("rows")[0][0].GetInt32());
        Assert.Equal(125250, count.GetProperty("rows")[0][1].GetInt32());
        Assert.NotEqual(JsonValueKind.Undefined, copyTo.ValueKind);
        Assert.NotEqual(JsonValueKind.Undefined, copyFrom.ValueKind);

        // The run deletes its staging files at the end: every object below the run prefix goes.
        foreach (var key in keys)
        {
            Assert.True(await store.DeleteAsync(key, Ct));
        }

        Assert.False((await ToList(store.ListAsync(keyBuilder.RunPrefix(ArtifactCategories.CopyStaging, "sbeg", runId), Ct))).Any());
    }

    private static string WithClause(ArchiveCopyStagingLocation location, string extra)
    {
        var withOptions = location.RevealOptions().Select(o => $"{o.Key} = '{o.Value}'").Append(extra);
        return string.Join(", ", withOptions);
    }

    private static async Task<JsonElement> Sql(CrateDbMinioFixture fixture, HttpClient http, string statement, ArchiveCopyStagingLocation? location)
    {
        using var response = await http.PostAsJsonAsync(fixture.CrateSqlUrl, new { stmt = statement }, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        if (!response.IsSuccessStatusCode)
        {
            // The error text of CrateDB can echo the statement, which carries the credential: scrub it.
            var shown = location?.Scrub(body) ?? body;
            Assert.Fail($"CrateDB refused the statement ({(int)response.StatusCode}): {shown}");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<List<T>> ToList<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
