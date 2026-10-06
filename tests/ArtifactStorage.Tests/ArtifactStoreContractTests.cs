using System.Security.Cryptography;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Time.Testing;

namespace ArtifactStorage.Tests;

/// <summary>
///     Provider-agnostic behaviour every <see cref="IArtifactStore" /> must show. Each provider test class derives
///     from this class; every test works below its own random prefix, so tests can share one bucket/container.
/// </summary>
public abstract class ArtifactStoreContractTests
{
    /// <summary>Size that forces several S3 multipart parts with the 5 MiB test part size.</summary>
    private const int LargeContentSize = 11 * 1024 * 1024 + 123;

    private readonly string _root = $"contract-{Guid.NewGuid():N}";

    /// <summary>Creates the store under test. <paramref name="timeProvider" /> drives retention only.</summary>
    protected abstract Task<IArtifactStore> CreateStoreAsync(TimeProvider? timeProvider = null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Key(string relative)
    {
        return $"{_root}/{relative}";
    }

    [Fact]
    public async Task PutThenOpenRead_ReturnsSameContent()
    {
        var store = await CreateStoreAsync();
        var data = RandomBytes(4096);

        await store.PutAsync(Key("a/file.bin"), new MemoryStream(data), ArtifactMetadata.None, Ct);

        await using var stream = await store.OpenReadAsync(Key("a/file.bin"), Ct);
        Assert.NotNull(stream);
        Assert.Equal(data, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Put_EmptyContent_RoundTrips()
    {
        var store = await CreateStoreAsync();

        await store.PutAsync(Key("empty.bin"), new MemoryStream(), ArtifactMetadata.None, Ct);

        var info = await store.GetInfoAsync(Key("empty.bin"), Ct);
        Assert.NotNull(info);
        Assert.Equal(0, info.Size);
        await using var stream = await store.OpenReadAsync(Key("empty.bin"), Ct);
        Assert.NotNull(stream);
        Assert.Empty(await ReadAllAsync(stream));
    }

    [Fact]
    public async Task Put_LargeNonSeekableStream_IsStreamedAndRoundTrips()
    {
        var store = await CreateStoreAsync();
        var data = RandomBytes(LargeContentSize);

        await store.PutAsync(Key("large/dump.octoenc"), new NonSeekableStream(new MemoryStream(data)),
            new ArtifactMetadata("application/octet-stream"), Ct);

        var info = await store.GetInfoAsync(Key("large/dump.octoenc"), Ct);
        Assert.NotNull(info);
        Assert.Equal(LargeContentSize, info.Size);
        await using var stream = await store.OpenReadAsync(Key("large/dump.octoenc"), Ct);
        Assert.NotNull(stream);
        Assert.Equal(SHA256.HashData(data), await SHA256.HashDataAsync(stream, Ct));
    }

    [Fact]
    public async Task GetInfo_ReturnsSizeTimestampAndMetadata()
    {
        var store = await CreateStoreAsync();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var metadata = new ArtifactMetadata("application/gzip",
            new Dictionary<string, string> { ["kid"] = "k1", ["run_id"] = "0123456789abcdef" });

        await store.PutAsync(Key("info.tar.gz"), new MemoryStream(RandomBytes(100)), metadata, Ct);
        var info = await store.GetInfoAsync(Key("info.tar.gz"), Ct);

        Assert.NotNull(info);
        Assert.Equal(Key("info.tar.gz"), info.Key);
        Assert.Equal(100, info.Size);
        Assert.InRange(info.CreatedAt, before, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(TimeSpan.Zero, info.CreatedAt.Offset);
        Assert.NotNull(info.Metadata);
        Assert.Equal("application/gzip", info.Metadata.ContentType);
        Assert.Equal(2, info.Metadata.Properties.Count);
        Assert.Equal("k1", info.Metadata.Properties["kid"]);
        Assert.Equal("0123456789abcdef", info.Metadata.Properties["run_id"]);
    }

    [Fact]
    public async Task GetInfo_WithoutContentType_ReportsDefaultContentType()
    {
        var store = await CreateStoreAsync();

        await store.PutAsync(Key("plain.bin"), new MemoryStream([1]), ArtifactMetadata.None, Ct);
        var info = await store.GetInfoAsync(Key("plain.bin"), Ct);

        Assert.NotNull(info?.Metadata);
        Assert.Equal(ArtifactMetadata.DefaultContentType, info.Metadata.ContentType);
        Assert.Empty(info.Metadata.Properties);
    }

    [Fact]
    public async Task Put_ExistingKey_ReplacesContentAndMetadata()
    {
        var store = await CreateStoreAsync();
        await store.PutAsync(Key("over.bin"), new MemoryStream(RandomBytes(500)),
            new ArtifactMetadata("text/plain", new Dictionary<string, string> { ["old"] = "1" }), Ct);

        var replacement = RandomBytes(42);
        await store.PutAsync(Key("over.bin"), new MemoryStream(replacement), ArtifactMetadata.None, Ct);

        var info = await store.GetInfoAsync(Key("over.bin"), Ct);
        Assert.NotNull(info?.Metadata);
        Assert.Equal(42, info.Size);
        Assert.Equal(ArtifactMetadata.DefaultContentType, info.Metadata.ContentType);
        Assert.Empty(info.Metadata.Properties);
        await using var stream = await store.OpenReadAsync(Key("over.bin"), Ct);
        Assert.Equal(replacement, await ReadAllAsync(stream!));
    }

    [Fact]
    public async Task MissingArtifact_ReturnsNullAndDeleteReturnsFalse()
    {
        var store = await CreateStoreAsync();

        Assert.Null(await store.OpenReadAsync(Key("missing/file.bin"), Ct));
        Assert.Null(await store.GetInfoAsync(Key("missing/file.bin"), Ct));
        Assert.False(await store.DeleteAsync(Key("missing/file.bin"), Ct));
    }

    [Fact]
    public async Task Delete_ExistingArtifact_RemovesItOnce()
    {
        var store = await CreateStoreAsync();
        await store.PutAsync(Key("del/file.bin"), new MemoryStream([1, 2, 3]), ArtifactMetadata.None, Ct);

        Assert.True(await store.DeleteAsync(Key("del/file.bin"), Ct));
        Assert.False(await store.DeleteAsync(Key("del/file.bin"), Ct));
        Assert.Null(await store.OpenReadAsync(Key("del/file.bin"), Ct));
        Assert.Empty(await ListKeysAsync(store, Key("del/")));
    }

    [Fact]
    public async Task List_ReturnsOnlyKeysWithPrefix()
    {
        var store = await CreateStoreAsync();
        string[] keys =
        [
            Key("presweep/tenant-a/one.octoenc"),
            Key("presweep/tenant-a/two.octoenc"),
            Key("presweep/tenant-ab/three.octoenc"),
            Key("tenant-dumps/tenant-a/four.tar.gz")
        ];
        foreach (var key in keys)
        {
            await store.PutAsync(key, new MemoryStream([7, 7]), new ArtifactMetadata("text/plain"), Ct);
        }

        Assert.Equal(keys[..2], await ListKeysAsync(store, Key("presweep/tenant-a/")));
        Assert.Equal(keys[..3], await ListKeysAsync(store, Key("presweep/tenant-a")));
        Assert.Equal(keys, await ListKeysAsync(store, $"{_root}/"));
        Assert.Empty(await ListKeysAsync(store, Key("restore-staging/")));

        var infos = await ToListAsync(store.ListAsync(Key("tenant-dumps/"), Ct));
        var single = Assert.Single(infos);
        Assert.Equal(2, single.Size);
        Assert.Null(single.Metadata);
        Assert.True(single.CreatedAt > DateTimeOffset.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task DeleteOlderThan_DeletesOnlyExpiredArtifactsBelowPrefix()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = await CreateStoreAsync(clock);
        await store.PutAsync(Key("presweep/t1/a.octoenc"), new MemoryStream([1]), ArtifactMetadata.None, Ct);
        await store.PutAsync(Key("presweep/t2/b.octoenc"), new MemoryStream([1]), ArtifactMetadata.None, Ct);
        await store.PutAsync(Key("tenant-dumps/t1/c.tar.gz"), new MemoryStream([1]), ArtifactMetadata.None, Ct);

        // Nothing is older than one hour yet (generous margin for clock skew between host and container).
        Assert.Empty(await store.DeleteOlderThanAsync(Key("presweep/"), TimeSpan.FromHours(1), Ct));

        clock.Advance(TimeSpan.FromHours(3));
        var deleted = await store.DeleteOlderThanAsync(Key("presweep/"), TimeSpan.FromHours(1), Ct);

        Assert.Equal([Key("presweep/t1/a.octoenc"), Key("presweep/t2/b.octoenc")], deleted.Order(StringComparer.Ordinal));
        Assert.Empty(await ListKeysAsync(store, Key("presweep/")));
        Assert.Equal([Key("tenant-dumps/t1/c.tar.gz")], await ListKeysAsync(store, Key("tenant-dumps/")));
    }

    [Fact]
    public async Task KeyBuilderKeys_WorkWithTheStore()
    {
        var store = await CreateStoreAsync();
        var builder = new ArtifactKeyBuilder($"{_root}/test-2-main");
        var key = builder.Build(ArtifactCategories.Presweep, "MyTenant", "mytenant-20261006T120000Z.presweep.octoenc");

        await store.PutAsync(key, new MemoryStream([9]), ArtifactMetadata.None, Ct);

        Assert.Equal([key], await ListKeysAsync(store, builder.TenantPrefix(ArtifactCategories.Presweep, "MYTENANT")));
        Assert.True(builder.TryParse(key, out var parts));
        Assert.Equal(new ArtifactKeyParts("presweep", "mytenant", "mytenant-20261006T120000Z.presweep.octoenc"), parts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("../escape")]
    [InlineData("a/./b")]
    [InlineData(".hidden")]
    [InlineData("a\\b")]
    [InlineData("a/b c")]
    [InlineData("a/ü")]
    public async Task InvalidKeys_AreRejected(string key)
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(key, new MemoryStream([1]), ArtifactMetadata.None, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(key, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetInfoAsync(key, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(key, Ct));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("/x")]
    [InlineData("a//")]
    public async Task InvalidPrefixes_AreRejected(string prefix)
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(async () => await ToListAsync(store.ListAsync(prefix, Ct)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteOlderThanAsync(prefix, TimeSpan.FromDays(1), Ct));
    }

    protected static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    protected static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    protected static async Task<string[]> ListKeysAsync(IArtifactStore store, string prefix)
    {
        return (await ToListAsync(store.ListAsync(prefix, Ct))).Select(i => i.Key).Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
