using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.S3;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ArtifactStorage.Tests;

/// <summary>
///     The staging location factory (AB#6969): S3 and Azure URIs as CrateDB sees them, the second endpoint, the
///     container SAS, validation and the rule that no credential leaves the location.
/// </summary>
public sealed class ArchiveCopyStagingLocationFactoryTests
{
    // Obviously fake credentials; the characters are the ones that break naive URIs.
    private const string CanaryKey = "AKIACANARY/0123@host";
    private const string CanarySecret = "S3cr3t+CANARY/key@with";
    private const string CanaryAccountKey = "Q0FOQVJZLWFjY291bnQta2V5LW5vdC1yZWFsLWJhc2U2NA==";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    private static ArtifactStorageOptions S3Options(Action<S3ArtifactStorageOptions>? configure = null)
    {
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.S3,
            InstancePrefix = "main",
            S3 =
            {
                ServiceUrl = "http://localhost:9000",
                CrateDbServiceUrl = "http://minio.octo-infra:9000",
                Region = "us-east-1",
                Bucket = "octo-artifacts",
                AccessKeyId = CanaryKey,
                SecretAccessKey = CanarySecret,
                ForcePathStyle = true
            }
        };
        configure?.Invoke(options.S3);
        return options;
    }

    private static ArtifactStorageOptions AzureOptions(Action<AzureBlobArtifactStorageOptions>? configure = null)
    {
        var options = new ArtifactStorageOptions
        {
            Provider = ArtifactStorageProvider.AzureBlob,
            InstancePrefix = "main",
            AzureBlob =
            {
                AccountUrl = "https://mmstaging1artifacts.blob.core.windows.net",
                AccountKey = CanaryAccountKey,
                Container = "octo-artifacts"
            }
        };
        configure?.Invoke(options.AzureBlob);
        return options;
    }

    internal static ArchiveCopyStagingLocationFactory CreateFactory(ArtifactStorageOptions options,
        ILogger<ArchiveCopyStagingLocationFactory>? logger = null)
    {
        return new ArchiveCopyStagingLocationFactory(Options.Create(options), new ArtifactKeyBuilder(options.InstancePrefix),
            new FakeTimeProvider(Now), logger);
    }

    private static (string Key, string Secret, string Authority, string Path) ParseS3Uri(string uri)
    {
        Assert.StartsWith("s3://", uri);
        var rest = uri["s3://".Length..];
        var at = rest.LastIndexOf('@');
        var userInfo = rest[..at];
        var colon = userInfo.IndexOf(':');
        var remainder = rest[(at + 1)..];
        var slash = remainder.IndexOf('/');
        return (Uri.UnescapeDataString(userInfo[..colon]), Uri.UnescapeDataString(userInfo[(colon + 1)..]),
            remainder[..slash], remainder[(slash + 1)..]);
    }

    [Fact]
    public void S3_UsesTheCrateDbEndpointAndProtocol_ButTheBotClientKeepsServiceUrl()
    {
        var options = S3Options();
        var factory = CreateFactory(options);

        var location = factory.Create("Sbeg", "r1", ArchiveCopyStagingAccess.Dump);

        var uri = ParseS3Uri(location.RevealUri());
        Assert.Equal("minio.octo-infra:9000", uri.Authority);
        Assert.Equal("octo-artifacts/main/copy-staging/sbeg/r1", uri.Path);
        Assert.Equal("s3", location.Scheme);
        Assert.Equal(new Dictionary<string, string> { ["protocol"] = "http" }, location.RevealOptions());

        // The bot side still talks to ServiceUrl: the options are not rewritten and the client is built from them.
        Assert.Equal("http://localhost:9000", options.S3.ServiceUrl);
        using var client = S3ArtifactStore.CreateClient(options.S3);
        Assert.StartsWith("http://localhost:9000", client.Config.ServiceURL);
    }

    [Fact]
    public void S3_HttpsCrateDbEndpoint_SetsProtocolHttpsAndExplicitDefaultPort()
    {
        var factory = CreateFactory(S3Options(s3 => s3.CrateDbServiceUrl = "https://objects.example.org"));

        var location = factory.Create("t", "r", ArchiveCopyStagingAccess.Restore);

        Assert.Equal("objects.example.org:443", ParseS3Uri(location.RevealUri()).Authority);
        Assert.Equal("https", location.RevealOptions()["protocol"]);
    }

    [Fact]
    public void S3_WithoutCrateDbEndpoint_UsesServiceUrl()
    {
        var factory = CreateFactory(S3Options(s3 =>
        {
            s3.CrateDbServiceUrl = null;
            s3.ServiceUrl = "https://fsn1.your-objectstorage.com";
        }));

        var location = factory.Create("t", "r", ArchiveCopyStagingAccess.Dump);

        Assert.Equal("fsn1.your-objectstorage.com:443", ParseS3Uri(location.RevealUri()).Authority);
        Assert.Equal("https", location.RevealOptions()["protocol"]);
    }

    [Fact]
    public void S3_KeyAndSecretWithSpecialCharacters_ParseBackToTheSameValues()
    {
        var factory = CreateFactory(S3Options());

        var uri = ParseS3Uri(factory.Create("t", "r", ArchiveCopyStagingAccess.Dump).RevealUri());

        Assert.Equal(CanaryKey, uri.Key);
        Assert.Equal(CanarySecret, uri.Secret);
    }

    [Fact]
    public void S3_WithoutEndpoint_UsesTheDefaultAwsEndpointAndNoProtocolOption()
    {
        var factory = CreateFactory(S3Options(s3 =>
        {
            s3.CrateDbServiceUrl = null;
            s3.ServiceUrl = null;
            s3.Region = "eu-central-1";
        }));

        var location = factory.Create("t", "r", ArchiveCopyStagingAccess.Dump);

        Assert.StartsWith("s3://", location.RevealUri());
        Assert.EndsWith("@octo-artifacts/main/copy-staging/t/r", location.RevealUri());
        Assert.Empty(location.RevealOptions());
    }

    [Fact]
    public void S3_WithoutStaticKeys_HasNoUserInfo()
    {
        var factory = CreateFactory(S3Options(s3 =>
        {
            s3.AccessKeyId = null;
            s3.SecretAccessKey = null;
        }));

        var uri = factory.Create("t", "r", ArchiveCopyStagingAccess.Dump).RevealUri();

        Assert.Equal("s3://minio.octo-infra:9000/octo-artifacts/main/copy-staging/t/r", uri);
    }

    [Fact]
    public void Create_AppendsTheSubPathAndLowerCasesTheTenant()
    {
        var factory = CreateFactory(S3Options());

        var location = factory.Create("SBEG", "r1", ArchiveCopyStagingAccess.Dump, "rt1/data/slice-0001");

        Assert.EndsWith("/octo-artifacts/main/copy-staging/sbeg/r1/rt1/data/slice-0001", location.RevealUri());
    }

    [Theory]
    [InlineData("", "r")]
    [InlineData("t.x", "r")]
    [InlineData("t", "")]
    [InlineData("t", "../r")]
    [InlineData("t", "a/b")]
    public void Create_RejectsInvalidTenantAndRunIds(string tenantId, string runId)
    {
        var factory = CreateFactory(S3Options());

        Assert.Throws<ArgumentException>(() => factory.Create(tenantId, runId, ArchiveCopyStagingAccess.Dump));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    public void Create_RejectsInvalidSubPaths(string subPath)
    {
        var factory = CreateFactory(S3Options());

        Assert.Throws<ArgumentException>(() => factory.Create("t", "r", ArchiveCopyStagingAccess.Dump, subPath));
    }

    [Fact]
    public void S3_LocationPrefixIsTheKeyPrefixOfTheArtifactKeyBuilder()
    {
        var factory = CreateFactory(S3Options());
        var builder = new ArtifactKeyBuilder("main");

        var uri = ParseS3Uri(factory.Create("Sbeg", "r1", ArchiveCopyStagingAccess.Dump).RevealUri());

        Assert.Equal("octo-artifacts/" + builder.RunPrefix(ArtifactCategories.CopyStaging, "sbeg", "r1").TrimEnd('/'),
            uri.Path);
    }

    [Theory]
    [InlineData("key:id", "secret")]
    [InlineData("keyid", "sec:ret")]
    public void S3_KeyOrSecretWithColon_IsNotSupportedAndNamesTheSettingsOnly(string key, string secret)
    {
        var factory = CreateFactory(S3Options(s3 =>
        {
            s3.AccessKeyId = key;
            s3.SecretAccessKey = secret;
        }));

        Assert.False(factory.IsSupported);
        var e = Assert.Throws<ArchiveCopyStagingNotSupportedException>(() =>
            factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));
        Assert.Contains("S3:SecretAccessKey", e.Message);
        Assert.DoesNotContain(key, e.Message);
        Assert.DoesNotContain(secret, e.Message);
    }

    [Fact]
    public void FileSystem_IsNotSupported()
    {
        var factory = CreateFactory(new ArtifactStorageOptions { Provider = ArtifactStorageProvider.FileSystem });

        Assert.False(factory.IsSupported);
        Assert.Contains("FileSystem", factory.UnsupportedReason);
        Assert.Contains("format 1", factory.UnsupportedReason);
        var e = Assert.Throws<ArchiveCopyStagingNotSupportedException>(() =>
            factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));
        Assert.Equal(factory.UnsupportedReason, e.Message);
    }

    [Fact]
    public void S3WithoutHttpEndpoint_IsNotSupported()
    {
        var factory = CreateFactory(S3Options(s3 =>
        {
            s3.CrateDbServiceUrl = null;
            s3.ServiceUrl = "minio:9000";
        }));

        Assert.False(factory.IsSupported);
        Assert.Contains("S3:ServiceUrl", factory.UnsupportedReason);
    }

    [Fact]
    public void Azure_UriHasAccountContainerAndPrefix_AndTheCredentialIsNotInTheUri()
    {
        var factory = CreateFactory(AzureOptions());

        var location = factory.Create("Sbeg", "r1", ArchiveCopyStagingAccess.Dump, "rt1/data/slice-0001");

        Assert.Equal(
            "az://mmstaging1artifacts.blob.core.windows.net/octo-artifacts/main/copy-staging/sbeg/r1/rt1/data/slice-0001",
            location.RevealUri());
        Assert.Equal("az", location.Scheme);
        Assert.Equal(["sas_token"], location.RevealOptions().Keys);
        Assert.DoesNotContain(CanaryAccountKey, location.RevealUri());
        Assert.DoesNotContain(location.RevealOptions()["sas_token"], location.RevealUri());
        Assert.DoesNotContain(CanaryAccountKey, location.RevealOptions()["sas_token"]);
    }

    [Fact]
    public void Azure_ConnectionStringWithAccountKey_IsSupported()
    {
        var factory = CreateFactory(AzureOptions(azure =>
        {
            azure.AccountUrl = null;
            azure.AccountKey = null;
            azure.ConnectionString =
                $"DefaultEndpointsProtocol=https;AccountName=meshmakersprod2artifacts;AccountKey={CanaryAccountKey};EndpointSuffix=core.windows.net";
        }));

        var location = factory.Create("t", "r", ArchiveCopyStagingAccess.Restore);

        Assert.True(factory.IsSupported);
        Assert.StartsWith("az://meshmakersprod2artifacts.blob.core.windows.net/octo-artifacts/", location.RevealUri());
    }

    [Fact]
    public void Azure_Sas_DumpHasCreateWriteListHttpsOnlyAndTheDefaultLifetime()
    {
        var factory = CreateFactory(AzureOptions());

        var sas = DecodeSas(factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));

        Assert.Equal("c", sas.Resource);
        Assert.Equal("cwl", sas.Permissions);
        Assert.Equal(SasProtocol.Https, sas.Protocol);
        Assert.True(sas.StartsOn <= Now);
        Assert.Equal(Now + TimeSpan.FromHours(2), sas.ExpiresOn);
        Assert.False(string.IsNullOrEmpty(sas.Signature));
    }

    [Fact]
    public void Azure_Sas_RestoreHasReadAndListOnly()
    {
        var factory = CreateFactory(AzureOptions());

        var sas = DecodeSas(factory.Create("t", "r", ArchiveCopyStagingAccess.Restore));

        Assert.Equal("rl", sas.Permissions);
        Assert.Equal(SasProtocol.Https, sas.Protocol);
    }

    [Fact]
    public void Azure_Sas_FollowsTheConfiguredLifetime()
    {
        var factory = CreateFactory(AzureOptions(azure => azure.StagingSasLifetime = TimeSpan.FromMinutes(30)));

        var sas = DecodeSas(factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));

        Assert.Equal(Now + TimeSpan.FromMinutes(30), sas.ExpiresOn);
    }

    [Fact]
    public void Azure_Sas_IsIssuedPerCall()
    {
        var factory = CreateFactory(AzureOptions());

        var dump = factory.Create("t", "r", ArchiveCopyStagingAccess.Dump).RevealOptions()["sas_token"];
        var restore = factory.Create("t", "r", ArchiveCopyStagingAccess.Restore).RevealOptions()["sas_token"];

        Assert.NotEqual(dump, restore);
    }

    [Fact]
    public void Azure_PlainHttpEmulatorEndpoint_AllowsHttpInTheSasAndSetsProtocol()
    {
        var factory = CreateFactory(AzureOptions(azure =>
        {
            azure.AccountUrl = "http://127.0.0.1:10000/devstoreaccount1";
            azure.AccountKey = CanaryAccountKey;
        }));

        var location = factory.Create("t", "r", ArchiveCopyStagingAccess.Dump);

        Assert.StartsWith("az://127.0.0.1:10000/devstoreaccount1/octo-artifacts/", location.RevealUri());
        Assert.Equal("http", location.RevealOptions()["protocol"]);
        Assert.Equal(SasProtocol.HttpsAndHttp, DecodeSas(location).Protocol);
    }

    [Fact]
    public void Azure_ManagedIdentity_IsNotSupportedAndNamesPhase2()
    {
        var factory = CreateFactory(AzureOptions(azure =>
        {
            azure.AccountKey = null;
            azure.UseManagedIdentity = true;
            azure.ManagedIdentityClientId = "11111111-CANARY-client";
        }));

        Assert.False(factory.IsSupported);
        var e = Assert.Throws<ArchiveCopyStagingNotSupportedException>(() =>
            factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));
        Assert.Contains("phase 2", e.Message);
        Assert.Contains("managed identity", e.Message);
        Assert.DoesNotContain("CANARY", e.Message);
        Assert.Equal(factory.UnsupportedReason, e.Message);
    }

    [Fact]
    public void Azure_ConnectionStringWithoutAccountKey_IsNotSupportedAndLeaksNothing()
    {
        var connectionString =
            "DefaultEndpointsProtocol=https;AccountName=acct;SharedAccessSignature=sv=CANARY-sas-token;EndpointSuffix=core.windows.net";
        var factory = CreateFactory(AzureOptions(azure =>
        {
            azure.AccountUrl = null;
            azure.AccountKey = null;
            azure.ConnectionString = connectionString;
        }));

        Assert.False(factory.IsSupported);
        var e = Assert.Throws<ArchiveCopyStagingNotSupportedException>(() =>
            factory.Create("t", "r", ArchiveCopyStagingAccess.Dump));
        Assert.Contains("phase 2", e.Message);
        Assert.DoesNotContain("CANARY", e.Message);
        Assert.DoesNotContain(connectionString, e.Message);
    }

    [Fact]
    public void LocationsNeverShowKeySecretOrToken()
    {
        foreach (var (location, secrets) in new[]
                 {
                     (CreateFactory(S3Options()).Create("t", "r", ArchiveCopyStagingAccess.Dump, "rt1"),
                         new[] { CanaryKey, CanarySecret, Uri.EscapeDataString(CanaryKey), Uri.EscapeDataString(CanarySecret) }),
                     (CreateFactory(AzureOptions()).Create("t", "r", ArchiveCopyStagingAccess.Dump, "rt1"),
                         new[] { CanaryAccountKey })
                 })
        {
            var sasToken = location.RevealOptions().GetValueOrDefault("sas_token");
            var shown = new List<string>
            {
                location.ToString(),
                location.RedactedUri,
                string.Join(';', location.RedactedOptions.Select(o => $"{o.Key}={o.Value}")),
                $"{location}",
                JsonSerializer.Serialize(location),
                location.Scrub($"COPY t FROM '{location.RevealUri()}' WITH ({string.Join(", ", location.RevealOptions().Select(o => $"{o.Key} = '{o.Value}'"))})")
            };

            foreach (var text in shown)
            {
                Assert.All(secrets, secret => Assert.DoesNotContain(secret, text));
                if (sasToken != null)
                {
                    Assert.DoesNotContain(sasToken, text);
                    Assert.DoesNotContain("sig=", text);
                }
            }
        }
    }

    [Fact]
    public void TheFactoryLogsTheRedactedLocationOnly()
    {
        var log = new CapturingLogger<ArchiveCopyStagingLocationFactory>();
        var s3 = CreateFactory(S3Options(), log).Create("t", "r", ArchiveCopyStagingAccess.Dump);
        var azure = CreateFactory(AzureOptions(), log).Create("t", "r", ArchiveCopyStagingAccess.Restore);

        Assert.NotEmpty(log.Messages);
        var all = string.Join('\n', log.Messages);
        Assert.Contains("minio.octo-infra:9000", all);
        Assert.DoesNotContain(CanaryKey, all);
        Assert.DoesNotContain(CanarySecret, all);
        Assert.DoesNotContain(Uri.EscapeDataString(CanarySecret), all);
        Assert.DoesNotContain(CanaryAccountKey, all);
        Assert.DoesNotContain(azure.RevealOptions()["sas_token"], all);
        Assert.DoesNotContain("sig=", all);
        Assert.NotNull(s3);
    }

    [Fact]
    public void AddArtifactStorage_RegistersTheFactory()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ArtifactStorage:Provider"] = "S3",
            ["ArtifactStorage:S3:Bucket"] = "b",
            ["ArtifactStorage:S3:ServiceUrl"] = "http://localhost:9000",
            ["ArtifactStorage:S3:CrateDbServiceUrl"] = "http://minio.octo-infra:9000"
        }).Build();
        using var provider = new ServiceCollection().AddArtifactStorage(configuration).BuildServiceProvider();

        var factory = provider.GetRequiredService<IArchiveCopyStagingLocationFactory>();

        Assert.True(factory.IsSupported);
        Assert.Contains("minio.octo-infra:9000", factory.Create("t", "r", ArchiveCopyStagingAccess.Dump).RevealUri());
    }

    [Fact]
    public void CrateDbServiceUrl_IsBoundFromTheEnvironmentVariable()
    {
        const string variable = "OCTO_ARTIFACTSTORAGE__S3__CRATEDBSERVICEURL";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "http://minio.octo-infra:9000");
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables("OCTO_").Build();

            var options = configuration.GetSection(ArtifactStorageOptions.SectionName).Get<ArtifactStorageOptions>();

            Assert.Equal("http://minio.octo-infra:9000", options?.S3.CrateDbServiceUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void StagingSasLifetime_DefaultsToTwoHoursAndBindsFromConfiguration()
    {
        Assert.Equal(TimeSpan.FromHours(2), new AzureBlobArtifactStorageOptions().StagingSasLifetime);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ArtifactStorage:AzureBlob:StagingSasLifetime"] = "00:30:00"
        }).Build();

        var options = configuration.GetSection(ArtifactStorageOptions.SectionName).Get<ArtifactStorageOptions>();

        Assert.Equal(TimeSpan.FromMinutes(30), options?.AzureBlob.StagingSasLifetime);
    }

    private static BlobSasQueryParameters DecodeSas(ArchiveCopyStagingLocation location)
    {
        var token = location.RevealOptions()["sas_token"];
        return new BlobUriBuilder(new Uri($"https://acct.blob.core.windows.net/c?{token}")).Sas!;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception != null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}

/// <summary>Start-up validation of the options of the archive copy staging (AB#6969).</summary>
public sealed class ArchiveCopyStagingOptionsValidationTests
{
    private static ValidateOptionsResult Validate(ArtifactStorageOptions options)
    {
        return new ArtifactStorageOptionsValidator().Validate(null, options);
    }

    private static ArtifactStorageOptions S3(string? crateDbServiceUrl) => new()
    {
        Provider = ArtifactStorageProvider.S3,
        S3 = { Bucket = "b", ServiceUrl = "http://localhost:9000", CrateDbServiceUrl = crateDbServiceUrl }
    };

    private static ArtifactStorageOptions Azure(TimeSpan lifetime, bool withConnectionString = false) => new()
    {
        Provider = ArtifactStorageProvider.AzureBlob,
        AzureBlob =
        {
            Container = "c",
            AccountUrl = "https://a.blob.core.windows.net",
            AccountKey = "ZmFrZQ==",
            ConnectionString = withConnectionString ? "AccountName=a;AccountKey=ZmFrZQ==" : null,
            StagingSasLifetime = lifetime
        }
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://minio.octo-infra:9000")]
    [InlineData("https://objects.example.org")]
    [InlineData("http://minio.octo-infra:9000/")]
    public void CrateDbServiceUrl_Valid(string? value)
    {
        Assert.True(Validate(S3(value)).Succeeded);
    }

    [Theory]
    [InlineData("minio.octo-infra:9000")]
    [InlineData("minio.octo-infra")]
    [InlineData("ftp://minio.octo-infra:9000")]
    [InlineData("s3://minio.octo-infra:9000")]
    [InlineData("http://minio.octo-infra:9000/bucket")]
    [InlineData("http://key:secret@minio.octo-infra:9000")]
    [InlineData("http://minio.octo-infra:9000?x=1")]
    public void CrateDbServiceUrl_Invalid_NamesTheOptionAndEchoesNoValue(string value)
    {
        var result = Validate(S3(value));

        Assert.True(result.Failed);
        var message = string.Join('\n', result.Failures!);
        Assert.Contains("ArtifactStorage:S3:CrateDbServiceUrl", message);
        Assert.DoesNotContain(value, message);
        Assert.DoesNotContain("secret", message);
    }

    [Fact]
    public void StagingSasLifetime_PositiveIsValid()
    {
        Assert.True(Validate(Azure(TimeSpan.FromMinutes(30))).Succeeded);
        Assert.True(Validate(Azure(TimeSpan.FromHours(2))).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void StagingSasLifetime_ZeroOrNegative_NamesTheOption(int minutes)
    {
        foreach (var withConnectionString in new[] { false, true })
        {
            var result = Validate(Azure(TimeSpan.FromMinutes(minutes), withConnectionString));

            Assert.True(result.Failed);
            Assert.Contains("ArtifactStorage:AzureBlob:StagingSasLifetime", string.Join('\n', result.Failures!));
        }
    }
}
