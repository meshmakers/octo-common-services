using Azure.Storage.Blobs;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;

namespace ArtifactStorage.Tests;

/// <summary>Runs the contract suite against the Azure Blob API (Azurite container).</summary>
[Trait("Category", "Container")]
public sealed class AzureBlobArtifactStoreAzuriteTests(AzuriteFixture fixture)
    : ArtifactStoreContractTests, IClassFixture<AzuriteFixture>
{
    protected override Task<IArtifactStore> CreateStoreAsync(TimeProvider? timeProvider = null)
    {
        fixture.SkipIfUnavailable();
        return Task.FromResult<IArtifactStore>(new AzureBlobArtifactStore(
            new BlobContainerClient(fixture.ConnectionString, AzuriteFixture.Container), false, timeProvider));
    }

    [Fact]
    public async Task OptionsWithConnectionString_CreateContainerOnFirstWrite()
    {
        fixture.SkipIfUnavailable();
        var store = new AzureBlobArtifactStore(new AzureBlobArtifactStorageOptions
        {
            ConnectionString = fixture.ConnectionString,
            Container = $"created-{Guid.NewGuid():N}",
            CreateContainerIfNotExists = true
        });

        await store.PutAsync("x/y.bin", new MemoryStream([1]), ArtifactMetadata.None,
            TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetInfoAsync("x/y.bin", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OptionsWithAccountUrlAndAccountKey_Authenticate()
    {
        fixture.SkipIfUnavailable();
        var accountKey = fixture.ConnectionString.Split(';')
            .Single(p => p.StartsWith("AccountKey=", StringComparison.Ordinal))["AccountKey=".Length..];
        var store = new AzureBlobArtifactStore(new AzureBlobArtifactStorageOptions
        {
            AccountUrl = fixture.BlobEndpoint,
            AccountKey = accountKey,
            Container = AzuriteFixture.Container
        });
        var key = $"key-auth-{Guid.NewGuid():N}/file.bin";

        await store.PutAsync(key, new MemoryStream([1, 2]), ArtifactMetadata.None,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, (await store.GetInfoAsync(key, TestContext.Current.CancellationToken))?.Size);
    }

    [Fact]
    public async Task MissingContainer_IsAnErrorNotNotFound()
    {
        fixture.SkipIfUnavailable();
        var store = new AzureBlobArtifactStore(new AzureBlobArtifactStorageOptions
        {
            ConnectionString = fixture.ConnectionString,
            Container = $"missing-{Guid.NewGuid():N}"
        });

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() =>
            store.OpenReadAsync("x/y.bin", TestContext.Current.CancellationToken));
    }
}
