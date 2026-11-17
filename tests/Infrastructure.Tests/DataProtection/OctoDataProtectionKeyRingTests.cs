using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml.Linq;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.Infrastructure.DataProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.DataProtection;

/// <summary>
///     The shared Data Protection key ring: every "host" below is an own ASP.NET Data Protection stack (own service
///     provider, own in-memory key ring cache) on one shared store - a model of service replicas. All keys are
///     generated per test run, in memory.
/// </summary>
public sealed class OctoDataProtectionKeyRingTests : IDisposable
{
    private const string Purpose = "Infrastructure.Tests.DataProtection";
    private const string AssetRepository = "OctoAssetRepositoryServices";
    private const string Bot = "OctoBotServices";

    private readonly List<IDisposable> _disposables = [];
    private readonly InMemoryKeyRecordStore _store = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public void TwoReplicas_OfTheSameService_UnprotectEachOthersPayloads()
    {
        var keys = NewPlatformKeys();
        var replicaA = CreateHost(AssetRepository, keys);
        var replicaB = CreateHost(AssetRepository, keys);

        var payload = replicaA.GetDataProtector(Purpose).Protect("oidc-state");

        Assert.Equal("oidc-state", replicaB.GetDataProtector(Purpose).Unprotect(payload));
        Assert.Single(_store.Records);
    }

    [Fact]
    public void Keys_SurviveARestart_OfAllReplicas()
    {
        var keys = NewPlatformKeys();
        var payload = CreateHost(AssetRepository, keys).GetDataProtector(Purpose).Protect("session-cookie");

        // A new process: nothing of the first host is left but the store.
        var restarted = CreateHost(AssetRepository, keys);

        Assert.Equal("session-cookie", restarted.GetDataProtector(Purpose).Unprotect(payload));
    }

    [Fact]
    public void DifferentServices_CannotUnprotectEachOthersPayloads_AndKeepTheirOwnKeys()
    {
        var keys = NewPlatformKeys();
        var assetRepository = CreateHost(AssetRepository, keys);
        var bot = CreateHost(Bot, keys);

        var payload = assetRepository.GetDataProtector(Purpose).Protect("oidc-state");

        Assert.ThrowsAny<CryptographicException>(() => bot.GetDataProtector(Purpose).Unprotect(payload));
        bot.GetDataProtector(Purpose).Protect("bot-cookie");
        Assert.Equal(2, _store.Records.Count);
        Assert.Single(_store.Records, r => r.StoredName.StartsWith(AssetRepository + "/key-", StringComparison.Ordinal));
        Assert.Single(_store.Records, r => r.StoredName.StartsWith(Bot + "/key-", StringComparison.Ordinal));
        Assert.Single(KeysOf(bot));
        Assert.Single(KeysOf(assetRepository));
        Assert.NotEqual(KeysOf(bot).Single().KeyId, KeysOf(assetRepository).Single().KeyId);
    }

    [Fact]
    public void ARing_IgnoresUnprefixedIdentityKeys_AndKeysOfAnApplicationWithACommonPrefix()
    {
        var keys = NewPlatformKeys();
        var identityLike = CreateHost("Octo", keys, new InMemoryKeyRecordStore());
        identityLike.GetDataProtector(Purpose).Protect("x");
        var foreignKey = ((InMemoryKeyRecordStore)identityLike.GetRequiredService<IDataProtectionKeyRecordStore>())
            .Records.Single();

        // An identity key (no prefix), and a key of the application "Octo", whose name is a prefix of "OctoX".
        _store.Add(new DataProtectionKeyRecord(foreignKey.StoredName["Octo/".Length..], foreignKey.XmlData));
        _store.Add(foreignKey);

        Assert.Empty(KeysOf(CreateHost("OctoX", keys)));
        Assert.Single(KeysOf(CreateHost("Octo", keys)));
    }

    [Fact]
    public void WithAPlatformKeyRing_NewKeys_AreAlwaysStoredEncrypted()
    {
        var keys = NewPlatformKeys();
        var host = CreateHost(AssetRepository, keys);
        host.GetDataProtector(Purpose).Protect("cookie");
        host.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        Assert.Equal(2, _store.Records.Count);
        foreach (var record in _store.Records)
        {
            var xml = XElement.Parse(record.XmlData);
            Assert.False(DataProtectionKeyXml.ContainsUnencryptedSecret(xml));
            Assert.DoesNotContain("masterKey", record.XmlData, StringComparison.Ordinal);
            Assert.Contains("enc:v2:k1:", record.XmlData, StringComparison.Ordinal);
            var secret = xml.Descendants(DataProtectionKeyXml.EncryptedSecretElementName).Single();
            Assert.Equal(DataProtectionKeyXml.DecryptorTypeName,
                (string?)secret.Attribute(DataProtectionKeyXml.DecryptorTypeAttributeName));
        }
    }

    [Fact]
    public void TheVersionFreeDecryptorTypeName_ResolvesToTheDecryptor()
    {
        Assert.Equal(typeof(PlatformKeyRingXmlDecryptor), Type.GetType(DataProtectionKeyXml.DecryptorTypeName));
    }

    [Fact]
    public void AKeyEncryptedWithAnotherKeyRing_IsNotUsable_AndANewKeyIsCreated()
    {
        var payload = CreateHost(AssetRepository, NewPlatformKeys()).GetDataProtector(Purpose).Protect("cookie");

        // Same key id "k1", other key material (a database restored from another installation).
        var other = CreateHost(AssetRepository, NewPlatformKeys());

        Assert.ThrowsAny<CryptographicException>(() => other.GetDataProtector(Purpose).Unprotect(payload));
        Assert.Equal("fresh", other.GetDataProtector(Purpose).Unprotect(other.GetDataProtector(Purpose).Protect("fresh")));
    }

    [Fact]
    public void WithoutAPlatformKeyRing_NothingIsWrittenToTheSharedStore_AndTheLocalDefaultIsKept()
    {
        var host = CreateHost(AssetRepository, platformKeys: null);

        // No repository set: ASP.NET falls back to its default (a local key directory). The test does not protect
        // anything, so it writes no key file into the profile of the machine that runs it.
        Assert.Null(host.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Empty(_store.Records);
    }

    [Fact]
    public void TheRepository_RefusesToStoreAKey_WithoutAPlatformKeyRing()
    {
        var host = CreateHost(AssetRepository, platformKeys: null);
        var repository = host.GetRequiredService<OctoDataProtectionKeyRepository>();
        var plainKey = new XElement("key",
            new XElement("masterKey", new XAttribute(DataProtectionKeyXml.RequiresEncryptionAttributeName, "true"),
                new XElement("value", "generated-in-test")));

        Assert.Throws<InvalidOperationException>(() => repository.StoreElement(plainKey, "key-test"));
        Assert.Empty(_store.Records);
    }

    [Fact]
    public void BeforeTheSystemTenantExists_NothingIsStored()
    {
        _store.CanPersist = false;
        var host = CreateHost(AssetRepository, NewPlatformKeys());

        Assert.ThrowsAny<Exception>(() => host.GetDataProtector(Purpose).Protect("cookie"));
        Assert.Empty(_store.Records);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Octo/Asset")]
    [InlineData("Octo Asset")]
    public void AnInvalidApplicationName_IsRefused(string applicationName)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddOctoDataProtectionKeyRing(applicationName));
    }

    [Fact]
    public void StoredNames_OfIdentityAndOfServiceRings_NeverMatchEachOther()
    {
        const string identityKey = "key-5d2b3a40-56d3-4ea4-9b64-2a4f4d11a7e1";
        var serviceKey = DataProtectionKeyNames.ToStoredName(AssetRepository, identityKey);

        Assert.True(DataProtectionKeyNames.IsUnprefixed(identityKey));
        Assert.False(DataProtectionKeyNames.IsUnprefixed(serviceKey));
        Assert.False(DataProtectionKeyNames.BelongsTo(AssetRepository, identityKey));
        Assert.True(DataProtectionKeyNames.BelongsTo(AssetRepository, serviceKey));
        Assert.False(DataProtectionKeyNames.BelongsTo("OctoAsset", serviceKey));
        Assert.False(DataProtectionKeyNames.BelongsTo(AssetRepository + "X", serviceKey));
    }

    private static IReadOnlyCollection<IKey> KeysOf(IServiceProvider host)
    {
        return host.GetRequiredService<IKeyManager>().GetAllKeys();
    }

    private static Dictionary<string, string?> NewPlatformKeys()
    {
        return new Dictionary<string, string?>
        {
            ["SecretEncryption:Keys:k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["SecretEncryption:ActiveKeyId"] = "k1"
        };
    }

    /// <summary>A service replica: own Data Protection stack, own platform protector, on the given store.</summary>
    private ServiceProvider CreateHost(string applicationName, Dictionary<string, string?>? platformKeys,
        InMemoryKeyRecordStore? store = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(platformKeys ?? new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddRuntimeEngine();
        services.AddSingleton<IDataProtectionKeyRecordStore>(store ?? _store);
        services.AddOctoDataProtectionKeyRing(applicationName);
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        Assert.Equal(platformKeys != null, provider.GetRequiredService<ISecretAttributeProtector>().IsConfigured);
        return provider;
    }

    private sealed class InMemoryKeyRecordStore : IDataProtectionKeyRecordStore
    {
        private readonly ConcurrentDictionary<string, DataProtectionKeyRecord> _records = new(StringComparer.Ordinal);

        public bool CanPersist { get; set; } = true;

        public IReadOnlyList<DataProtectionKeyRecord> Records => _records.Values.ToList();

        public void Add(DataProtectionKeyRecord record)
        {
            _records[record.StoredName] = record;
        }

        public Task<bool> CanPersistAsync()
        {
            return Task.FromResult(CanPersist);
        }

        public Task<IReadOnlyList<DataProtectionKeyRecord>> LoadAllAsync()
        {
            return Task.FromResult(Records);
        }

        public Task InsertAsync(DataProtectionKeyRecord record)
        {
            if (!CanPersist)
            {
                throw new InvalidOperationException("not bootstrapped");
            }

            _records.TryAdd(record.StoredName, record);
            return Task.CompletedTask;
        }
    }
}
