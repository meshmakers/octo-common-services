using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Testcontainers.Azurite;
using Testcontainers.Minio;

namespace ArtifactStorage.Tests;

/// <summary>
///     Starts a container once per test class. When Docker is not available (or
///     <c>OCTO_SKIP_CONTAINER_TESTS=true</c>), the tests of that class are reported as skipped, not failed.
/// </summary>
public abstract class ContainerFixtureBase : IAsyncLifetime
{
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("OCTO_SKIP_CONTAINER_TESTS"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "OCTO_SKIP_CONTAINER_TESTS=true";
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await StartAsync(timeout.Token);
        }
        catch (Exception e)
        {
            SkipReason = $"Container could not be started (Docker unavailable?): {e.GetType().Name}: {e.Message}";
        }
    }

    public abstract ValueTask DisposeAsync();

    public void SkipIfUnavailable()
    {
        Assert.SkipWhen(SkipReason != null, SkipReason ?? string.Empty);
    }

    protected abstract Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
///     MinIO as the S3-compatible backend. The image is configurable with <c>OCTO_TEST_MINIO_IMAGE</c>; the
///     default is the Chainguard build, because MinIO no longer publishes images on Docker Hub.
/// </summary>
public sealed class MinioFixture : ContainerFixtureBase
{
    public const string DefaultImage = "cgr.dev/chainguard/minio:latest";
    public const string Bucket = "octo-artifacts-test";

    private MinioContainer? _container;

    public S3ArtifactStorageOptions Options { get; private set; } = new();

    protected override async Task StartAsync(CancellationToken cancellationToken)
    {
        var image = Environment.GetEnvironmentVariable("OCTO_TEST_MINIO_IMAGE");
        _container = new MinioBuilder(string.IsNullOrWhiteSpace(image) ? DefaultImage : image).Build();
        await _container.StartAsync(cancellationToken);

        Options = new S3ArtifactStorageOptions
        {
            ServiceUrl = _container.GetConnectionString(),
            Region = "us-east-1",
            Bucket = Bucket,
            AccessKeyId = _container.GetAccessKey(),
            SecretAccessKey = _container.GetSecretKey(),
            ForcePathStyle = true,
            MultipartPartSizeBytes = ArtifactStorageOptionsValidator.MinS3PartSize
        };
        using var client = S3ArtifactStore.CreateClient(Options);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Azurite as the Azure Blob backend.</summary>
public sealed class AzuriteFixture : ContainerFixtureBase
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.35.0";
    public const string Container = "octo-artifacts-test";

    private AzuriteContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public string BlobEndpoint { get; private set; } = string.Empty;

    protected override async Task StartAsync(CancellationToken cancellationToken)
    {
        _container = new AzuriteBuilder(Image)
            .WithInMemoryPersistence()
            .WithCommand("--skipApiVersionCheck")
            .Build();
        await _container.StartAsync(cancellationToken);
        ConnectionString = _container.GetConnectionString();
        BlobEndpoint = _container.GetBlobEndpoint();
        await new BlobContainerClient(ConnectionString, Container).CreateIfNotExistsAsync(
            cancellationToken: cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
