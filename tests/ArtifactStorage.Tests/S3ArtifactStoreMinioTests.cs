using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.S3;

namespace ArtifactStorage.Tests;

/// <summary>Runs the contract suite against a real S3 API (MinIO container).</summary>
[Trait("Category", "Container")]
public sealed class S3ArtifactStoreMinioTests(MinioFixture fixture)
    : ArtifactStoreContractTests, IClassFixture<MinioFixture>
{
    protected override Task<IArtifactStore> CreateStoreAsync(TimeProvider? timeProvider = null)
    {
        fixture.SkipIfUnavailable();
        return Task.FromResult<IArtifactStore>(new S3ArtifactStore(fixture.Options, timeProvider));
    }

    [Fact]
    public async Task Put_WithServerSideEncryptionAes256_IsAcceptedOrReportsServerSupport()
    {
        fixture.SkipIfUnavailable();
        var options = new S3ArtifactStorageOptions
        {
            ServiceUrl = fixture.Options.ServiceUrl,
            Region = fixture.Options.Region,
            Bucket = fixture.Options.Bucket,
            AccessKeyId = fixture.Options.AccessKeyId,
            SecretAccessKey = fixture.Options.SecretAccessKey,
            ForcePathStyle = true,
            MultipartPartSizeBytes = ArtifactStorageOptionsValidator.MinS3PartSize,
            ServerSideEncryption = "AES256"
        };
        using var store = new S3ArtifactStore(options);
        var key = $"sse-{Guid.NewGuid():N}/file.bin";

        try
        {
            await store.PutAsync(key, new MemoryStream([1, 2, 3]), ArtifactMetadata.None,
                TestContext.Current.CancellationToken);
        }
        catch (Amazon.S3.AmazonS3Exception e) when (e.ErrorCode is "NotImplemented" or "KMS.NotConfigured" or "InvalidRequest")
        {
            // A MinIO without a KMS rejects SSE-S3; the request was formed correctly.
            Assert.Skip($"The test server does not support SSE-S3: {e.ErrorCode}");
        }

        Assert.NotNull(await store.GetInfoAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateBucketIfNotExists_CreatesMissingBucket()
    {
        fixture.SkipIfUnavailable();
        var options = new S3ArtifactStorageOptions
        {
            ServiceUrl = fixture.Options.ServiceUrl,
            Region = fixture.Options.Region,
            Bucket = $"created-{Guid.NewGuid():N}"[..30],
            AccessKeyId = fixture.Options.AccessKeyId,
            SecretAccessKey = fixture.Options.SecretAccessKey,
            ForcePathStyle = true,
            CreateBucketIfNotExists = true
        };
        using var store = new S3ArtifactStore(options);

        await store.PutAsync("x/y.bin", new MemoryStream([1]), ArtifactMetadata.None,
            TestContext.Current.CancellationToken);

        Assert.NotNull(await store.GetInfoAsync("x/y.bin", TestContext.Current.CancellationToken));
    }
}
