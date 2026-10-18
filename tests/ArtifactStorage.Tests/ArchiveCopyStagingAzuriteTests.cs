using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ArtifactStorage.Tests;

/// <summary>
///     The container SAS of the archive copy staging location against the Azure Blob API (Azurite): the token is
///     accepted for the permitted operations and refused for the others (AB#6969).
/// </summary>
[Trait("Category", "Container")]
public sealed class ArchiveCopyStagingAzuriteTests(AzuriteFixture fixture) : IClassFixture<AzuriteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string _instancePrefix = string.Empty;

    private ArchiveCopyStagingLocationFactory CreateFactory(DateTimeOffset? now = null, TimeSpan? lifetime = null)
    {
        fixture.SkipIfUnavailable();
        var accountKey = fixture.ConnectionString.Split(';')
            .Single(p => p.StartsWith("AccountKey=", StringComparison.Ordinal))["AccountKey=".Length..];
        _instancePrefix = $"sas-{Guid.NewGuid():N}";
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            InstancePrefix = _instancePrefix,
            AzureBlob =
            {
                AccountUrl = fixture.BlobEndpoint,
                AccountKey = accountKey,
                Container = AzuriteFixture.Container,
                StagingSasLifetime = lifetime ?? TimeSpan.FromHours(2)
            }
        };
        return new ArchiveCopyStagingLocationFactory(Options.Create(options), new ArtifactKeyBuilder(options.InstancePrefix),
            new FakeTimeProvider(now ?? DateTimeOffset.UtcNow));
    }

    private BlobContainerClient ContainerWithToken(ArchiveCopyStagingLocation location)
    {
        var token = location.RevealOptions()["sas_token"];
        var container = new BlobUriBuilder(new Uri(fixture.BlobEndpoint)) { BlobContainerName = AzuriteFixture.Container }
            .ToUri();
        return new BlobContainerClient(new Uri($"{container}?{token}"));
    }

    private BlobContainerClient ContainerWithAccountKey()
    {
        return new BlobContainerClient(fixture.ConnectionString, AzuriteFixture.Container);
    }

    [Fact]
    public async Task DumpToken_AllowsWriteAndListButNotReadOrDelete()
    {
        var factory = CreateFactory();
        var location = factory.Create("sbeg", "r1", ArchiveCopyStagingAccess.Dump);
        var container = ContainerWithToken(location);
        var blobPath = $"{_instancePrefix}/copy-staging/sbeg/r1/rt1/data/s1/p0.json.gz";

        await container.GetBlobClient(blobPath).UploadAsync(new MemoryStream([1, 2, 3]), true, Ct);
        var listed = new List<string>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobPath[..blobPath.LastIndexOf('/')], Ct))
        {
            listed.Add(item.Name);
        }

        Assert.Equal([blobPath], listed);
        await AssertForbiddenAsync(async () => await container.GetBlobClient(blobPath).DownloadContentAsync(Ct));
        await AssertForbiddenAsync(async () => await container.GetBlobClient(blobPath).DeleteAsync(cancellationToken: Ct));

        // The written object is really there (checked with the account key).
        Assert.True((await ContainerWithAccountKey().GetBlobClient(blobPath).ExistsAsync(Ct)).Value);
    }

    [Fact]
    public async Task RestoreToken_AllowsReadAndListButNotWriteOrDelete()
    {
        var factory = CreateFactory();
        var location = factory.Create("sbeg", "r2", ArchiveCopyStagingAccess.Restore);
        var blobPath = $"{_instancePrefix}/copy-staging/sbeg/r2/rt1/data/s1/p0.json.gz";
        await ContainerWithAccountKey().GetBlobClient(blobPath).UploadAsync(new MemoryStream([4, 5, 6]), true, Ct);
        var container = ContainerWithToken(location);

        var downloaded = await container.GetBlobClient(blobPath).DownloadContentAsync(Ct);
        var listed = new List<string>();
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobPath[..blobPath.LastIndexOf('/')], Ct))
        {
            listed.Add(item.Name);
        }

        Assert.Equal([4, 5, 6], downloaded.Value.Content.ToArray());
        Assert.Equal([blobPath], listed);
        await AssertForbiddenAsync(async () =>
            await container.GetBlobClient(blobPath + ".x").UploadAsync(new MemoryStream([1]), true, Ct));
        await AssertForbiddenAsync(async () => await container.GetBlobClient(blobPath).DeleteAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task ExpiredToken_IsRefused()
    {
        var factory = CreateFactory(DateTimeOffset.UtcNow - TimeSpan.FromHours(3), TimeSpan.FromHours(2));
        var location = factory.Create("sbeg", "r3", ArchiveCopyStagingAccess.Dump);
        var container = ContainerWithToken(location);

        await AssertForbiddenAsync(async () =>
            await container.GetBlobClient("sas-expired/x.bin").UploadAsync(new MemoryStream([1]), true, Ct));
    }

    [Fact]
    public async Task TokenOfAnotherContainer_IsRefused()
    {
        var factory = CreateFactory();
        var location = factory.Create("sbeg", "r4", ArchiveCopyStagingAccess.Dump);
        var token = location.RevealOptions()["sas_token"];
        var other = new BlobUriBuilder(new Uri(fixture.BlobEndpoint)) { BlobContainerName = "another-container" }.ToUri();
        var container = new BlobContainerClient(new Uri($"{other}?{token}"));

        await AssertForbiddenAsync(async () =>
            await container.GetBlobClient("x.bin").UploadAsync(new MemoryStream([1]), true, Ct));
    }

    private static async Task AssertForbiddenAsync(Func<Task> operation)
    {
        var e = await Assert.ThrowsAsync<RequestFailedException>(operation);
        Assert.Contains(e.Status, new[] { (int)HttpStatusCode.Forbidden, (int)HttpStatusCode.Unauthorized });
    }
}
