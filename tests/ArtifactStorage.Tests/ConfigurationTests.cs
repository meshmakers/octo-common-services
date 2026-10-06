using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;
using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArtifactStorage.Tests;

public sealed class ConfigurationTests
{
    // Obviously fake values; no real credentials anywhere in tests.
    private const string FakeAccessKey = "test-access-key";
    private const string FakeSecret = "test-secret-not-real";

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddArtifactStorage(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Defaults_UseFileSystemInTempDirectory()
    {
        using var provider = Build(new Dictionary<string, string?>());

        var store = Assert.IsType<FileSystemArtifactStore>(provider.GetRequiredService<IArtifactStore>());
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "octo-artifacts")).TrimEnd(Path.DirectorySeparatorChar),
            store.RootPath);
        Assert.Equal("default", provider.GetRequiredService<ArtifactKeyBuilder>().InstancePrefix);
    }

    [Fact]
    public void FileSystem_UsesConfiguredRootAndInstancePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "octo-artifacts-config-test");
        using var provider = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:Provider"] = "FileSystem",
            ["ArtifactStorage:InstancePrefix"] = "test-2/main",
            ["ArtifactStorage:FileSystem:RootPath"] = root
        });

        var store = Assert.IsType<FileSystemArtifactStore>(provider.GetRequiredService<IArtifactStore>());
        Assert.Equal(Path.GetFullPath(root), store.RootPath);
        Assert.Equal("test-2/main", provider.GetRequiredService<ArtifactKeyBuilder>().InstancePrefix);
    }

    [Fact]
    public void S3_IsCreatedFromConfigurationWithoutNetworkAccess()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:Provider"] = "S3",
            ["ArtifactStorage:S3:ServiceUrl"] = "http://127.0.0.1:9",
            ["ArtifactStorage:S3:Region"] = "fsn1",
            ["ArtifactStorage:S3:Bucket"] = "bucket",
            ["ArtifactStorage:S3:AccessKeyId"] = FakeAccessKey,
            ["ArtifactStorage:S3:SecretAccessKey"] = FakeSecret,
            ["ArtifactStorage:S3:ForcePathStyle"] = "true",
            ["ArtifactStorage:S3:ServerSideEncryption"] = "AES256"
        });

        var store = provider.GetRequiredService<IArtifactStore>();
        Assert.IsType<S3ArtifactStore>(store);
        Assert.Equal("S3", store.ProviderName);
    }

    [Fact]
    public void AzureBlob_IsCreatedFromAccountUrlAndManagedIdentity()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:Provider"] = "AzureBlob",
            ["ArtifactStorage:AzureBlob:AccountUrl"] = "https://example.blob.core.windows.net",
            ["ArtifactStorage:AzureBlob:UseManagedIdentity"] = "true",
            ["ArtifactStorage:AzureBlob:Container"] = "octo-artifacts"
        });

        Assert.IsType<AzureBlobArtifactStore>(provider.GetRequiredService<IArtifactStore>());
    }

    [Fact]
    public void InvalidConfiguration_FailsValidationWithoutEchoingSecrets()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:Provider"] = "S3",
            ["ArtifactStorage:S3:SecretAccessKey"] = FakeSecret
        });

        var e = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ArtifactStorageOptions>>().Value);
        Assert.Contains(e.Failures, f => f.Contains("Bucket", StringComparison.Ordinal));
        Assert.Contains(e.Failures, f => f.Contains("AccessKeyId and", StringComparison.Ordinal));
        Assert.DoesNotContain(FakeSecret, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ChecksEachProvider()
    {
        var validator = new ArtifactStorageOptionsValidator();

        Assert.True(validator.Validate(null, new ArtifactStorageOptions()).Succeeded);
        Assert.False(validator.Validate(null, new ArtifactStorageOptions { InstancePrefix = "" }).Succeeded);
        Assert.False(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            S3 = { Bucket = "b", Region = "eu-central-1", ServerSideEncryption = "aws:kms" }
        }).Succeeded);
        Assert.False(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            S3 = { Bucket = "b", Region = "eu-central-1", MultipartPartSizeBytes = 1024 }
        }).Succeeded);
        Assert.True(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            S3 = { Bucket = "b", Region = "eu-central-1" }
        }).Succeeded);
        Assert.False(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            AzureBlob = { AccountUrl = "https://a.blob.core.windows.net", Container = "c" }
        }).Succeeded);
        Assert.False(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            AzureBlob = { ConnectionString = "UseDevelopmentStorage=true" }
        }).Succeeded);
        Assert.True(validator.Validate(null, new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            AzureBlob = { ConnectionString = "UseDevelopmentStorage=true", Container = "c" }
        }).Succeeded);
    }

    [Fact]
    public void EnvironmentStyleKeys_Bind()
    {
        // OCTO_ARTIFACTSTORAGE__S3__BUCKET arrives as "ArtifactStorage:S3:Bucket" after the OCTO_ prefix is stripped.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ARTIFACTSTORAGE:PROVIDER"] = "s3",
            ["ARTIFACTSTORAGE:S3:BUCKET"] = "bucket",
            ["ARTIFACTSTORAGE:S3:REGION"] = "eu-central-1"
        }).Build();
        using var provider = new ServiceCollection().AddArtifactStorage(configuration).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ArtifactStorageOptions>>().Value;
        Assert.Equal(ArtifactStorageProvider.S3, options.Provider);
        Assert.Equal("bucket", options.S3.Bucket);
    }
}
