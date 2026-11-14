using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>
///     AB#8969: the startup validation loads (or waits for) a validation tenant's CK cache instead of skipping a tenant whose
///     cache a CK model import unloaded just before, so services with CK methods start with several replicas.
/// </summary>
public class CkMethodValidationCacheLoadTests
{
    private const string Tenant = "octosystem";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The tenant database every replica shares: whether the model is installed, and whether a CK model import runs
    ///     (then a cache load fails like the engine's after its wait ran out, marked with the import-in-progress key).
    /// </summary>
    private sealed class TenantDatabase
    {
        private int _importsRunning;

        public volatile bool ModelInstalled = true;

        public bool ImportRunning => Volatile.Read(ref _importsRunning) > 0;

        /// <summary>One replica's import: installs the model and unloads that replica's cache (the engine does both).</summary>
        public async Task ImportAsync(Replica replica, TimeSpan duration)
        {
            Interlocked.Increment(ref _importsRunning);
            try
            {
                replica.Unload();
                await Task.Delay(duration);
                ModelInstalled = true;
            }
            finally
            {
                Interlocked.Decrement(ref _importsRunning);
            }

            replica.Unload();
        }
    }

    /// <summary>One service replica: its own process-local CK cache over the shared tenant database.</summary>
    private sealed class Replica : ICkMethodValidationCacheLoader
    {
        private readonly TenantDatabase _database;
        private volatile bool _loaded;
        private volatile bool _hasModel;

        public Replica(TenantDatabase database, bool loaded = false)
        {
            _database = database;
            _loaded = loaded;
            _hasModel = loaded && database.ModelInstalled;
            Cache = A.Fake<ICkCacheService>();
            A.CallTo(() => Cache.IsTenantLoaded(Tenant)).ReturnsLazily(() => _loaded);
            CkTypeGraph? ignored;
            A.CallTo(() => Cache.TryGetRtCkType(Tenant, A<RtCkId<CkTypeId>>._, out ignored))
                .ReturnsLazily(() =>
                {
                    if (UnloadOnNextLookup)
                    {
                        // a concurrent CK model import unloads the cache between IsTenantLoaded and the lookup
                        UnloadOnNextLookup = false;
                        _loaded = false;
                    }

                    // like CkCacheService: an unloaded tenant throws, it does not answer "not found"
                    if (!_loaded)
                    {
                        throw new CkCacheException($"CkCache for tenant '{Tenant}' not loaded.");
                    }

                    return _hasModel;
                })
                .AssignsOutAndRefParametersLazily(_ => [_hasModel ? UserGraph : null]);
        }

        public ICkCacheService Cache { get; }

        public int Loads;

        public volatile bool UnloadOnNextLookup;

        public void Unload() => _loaded = false;

        public async Task LoadAsync(string tenantId)
        {
            Interlocked.Increment(ref Loads);
            await Task.Yield();
            if (_database.ImportRunning)
            {
                var e = new InvalidOperationException("waited too long for a running CK model import");
                e.Data["Meshmakers.Octo.CkModelImportInProgress"] = "System.Identity";
                throw e;
            }

            _hasModel = _database.ModelInstalled;
            _loaded = true;
        }

        public CkCacheMethodDefinitionSource Source(ICkMethodValidationCacheLoader? loader = null) =>
            new(Cache, Options.Create(new CkMethodHandlerOptions()),
                Options.Create(new OctoSystemConfiguration { SystemTenantId = "OctoSystem" }), loader ?? this)
            {
                RetryInterval = TimeSpan.FromMilliseconds(10)
            };
    }

    private static readonly CkTypeGraph UserGraph = CreateUserGraph();

    private static CkTypeGraph CreateUserGraph()
    {
        var definition = TestData.Definition();
        return new CkTypeGraph(new CkId<CkTypeId>("System.Identity-1.0.0/User"), false, false, false, [], null, null, [], [],
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [], new CkGraphDirectedAssociations([]), "", false,
            allMethods: new Dictionary<string, CkMethodGraph>
            {
                [definition.MethodId] = new(new CkId<CkTypeId>("System.Identity-1.0.0/User"), definition)
            });
    }

    private static CkMethodHandlerRegistrations Registrations(string methodId = TestData.MethodId)
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        registrations.Add(methodId, typeof(ChangePasswordHandler));
        return registrations;
    }

    [Fact]
    public async Task CacheNotLoaded_IsLoadedOnDemand_AndTheServiceStarts()
    {
        var replica = new Replica(new TenantDatabase());
        var registrations = Registrations();

        await new CkMethodRegistrationValidator(replica.Source())
            .ValidateAsync(registrations, Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, replica.Loads);
        Assert.NotNull(registrations.Handlers[TestData.MethodId].Definition);
    }

    [Fact]
    public async Task CacheNotLoaded_WithoutLoader_IsAwaitedUntilAnotherPartLoadsIt()
    {
        var replica = new Replica(new TenantDatabase());
        var noLoader = A.Fake<ICkMethodValidationCacheLoader>();
        A.CallTo(() => noLoader.LoadAsync(A<string>._)).ThrowsAsync(new InvalidOperationException("no tenant context"));
        var registrations = Registrations();

        var validation = new CkMethodRegistrationValidator(replica.Source(noLoader))
            .ValidateAsync(registrations, Timeout, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(validation.IsCompleted);

        await replica.LoadAsync(Tenant); // e.g. the default configuration or another start step
        await validation;

        Assert.NotNull(registrations.Handlers[TestData.MethodId].Definition);
    }

    [Fact]
    public async Task UnknownMethod_StillFailsWithTheNoMethodDefinitionError()
    {
        var replica = new Replica(new TenantDatabase());

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CkMethodRegistrationValidator(replica.Source()).ValidateAsync(
                Registrations("System.Identity/User.Nope-1"), Timeout, TestContext.Current.CancellationToken));

        Assert.IsNotType<CkMethodDefinitionCacheNotLoadedException>(e);
        Assert.Contains("has no such method", e.Message);
        Assert.Contains("'octosystem'", e.Message);
    }

    [Fact]
    public async Task ModelNotInstalled_StillFailsWithTheNoMethodDefinitionError()
    {
        var replica = new Replica(new TenantDatabase { ModelInstalled = false });

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CkMethodRegistrationValidator(replica.Source()).ValidateAsync(Registrations(), Timeout,
                TestContext.Current.CancellationToken));

        Assert.IsNotType<CkMethodDefinitionCacheNotLoadedException>(e);
        Assert.Contains("has no such method", e.Message);
    }

    [Fact]
    public async Task CacheDoesNotLoadWithinTheTimeout_FailsNamingTheTenantAndTheCacheLoad()
    {
        var replica = new Replica(new TenantDatabase());
        var failing = A.Fake<ICkMethodValidationCacheLoader>();
        A.CallTo(() => failing.LoadAsync(A<string>._)).ThrowsAsync(new InvalidOperationException("mongo down"));

        var e = await Assert.ThrowsAsync<CkMethodDefinitionCacheNotLoadedException>(() =>
            new CkMethodRegistrationValidator(replica.Source(failing)).ValidateAsync(Registrations(),
                TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.Equal(Tenant, e.TenantId);
        Assert.Contains("'octosystem'", e.Message);
        Assert.Contains("CK cache", e.Message);
        Assert.Contains("did not load", e.Message);
        Assert.Contains("Methods:CacheLoadTimeout", e.Message);
        Assert.Contains("mongo down", e.Message);
        Assert.DoesNotContain("no such method", e.Message);
        A.CallTo(() => failing.LoadAsync(Tenant)).MustHaveHappened();
    }

    [Fact]
    public async Task HangingLoad_IsCutAtTheTimeout()
    {
        var replica = new Replica(new TenantDatabase());
        var hanging = A.Fake<ICkMethodValidationCacheLoader>();
        A.CallTo(() => hanging.LoadAsync(A<string>._)).Returns(new TaskCompletionSource().Task);

        var e = await Assert.ThrowsAsync<CkMethodDefinitionCacheNotLoadedException>(() =>
            new CkMethodRegistrationValidator(replica.Source(hanging)).ValidateAsync(Registrations(),
                TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));

        Assert.Equal(Tenant, e.TenantId);
    }

    [Fact]
    public void Find_OnAnUnloadedCache_ThrowsInsteadOfAnsweringNotFound()
    {
        var replica = new Replica(new TenantDatabase());

        Assert.Throws<CkMethodDefinitionCacheNotLoadedException>(() => replica.Source().Find(TestData.MethodId));
    }

    [Fact]
    public async Task CacheUnloadedBetweenTheLoadAndTheCheck_IsLoadedAgain()
    {
        var replica = new Replica(new TenantDatabase()) { UnloadOnNextLookup = true };
        var registrations = Registrations();

        await new CkMethodRegistrationValidator(replica.Source())
            .ValidateAsync(registrations, Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, replica.Loads);
        Assert.NotNull(registrations.Handlers[TestData.MethodId].Definition);
    }

    /// <summary>
    ///     Several replicas start on one fresh tenant at the same time: each imports the model (which unloads its own cache,
    ///     and the imports overlap so cache loads meet a running import), and an import of another replica unloads the cache
    ///     while this replica validates. All replicas must start. Before AB#8969 the validation skipped the unloaded tenant and
    ///     failed with "no such method".
    /// </summary>
    [Fact]
    public async Task SeveralReplicas_StartConcurrentlyOnAFreshTenant_AllStart()
    {
        var database = new TenantDatabase { ModelInstalled = false };
        var replicas = Enumerable.Range(0, 4).Select(_ => new Replica(database, loaded: true)).ToList();
        var ct = TestContext.Current.CancellationToken;

        var starts = replicas.Select(async (replica, i) =>
        {
            // start step order 12: the model import of this replica
            await database.ImportAsync(replica, TimeSpan.FromMilliseconds(30 + 20 * i));
            // start step order 15: the validation, while the next replica's import unloads this cache again
            var registrations = Registrations();
            var validation = new CkMethodRegistrationValidator(replica.Source())
                .ValidateAsync(registrations, Timeout, ct);
            var concurrentImport = database.ImportAsync(replica, TimeSpan.FromMilliseconds(40));
            await Task.WhenAll(validation, concurrentImport);
            return registrations.Handlers[TestData.MethodId].Definition;
        }).ToList();

        var definitions = await Task.WhenAll(starts);

        Assert.All(definitions, Assert.NotNull);
        Assert.All(replicas, r => Assert.True(r.Loads >= 1));
    }

    [Fact]
    public async Task ValidationRightAfterAnImport_NeverAnswersNotFound()
    {
        // before AB#8969 Find skipped the unloaded tenant and the start failed with "no such method"; now it is a cache error
        var database = new TenantDatabase { ModelInstalled = false };
        var replica = new Replica(database, loaded: true);
        await database.ImportAsync(replica, TimeSpan.FromMilliseconds(10));

        Assert.False(replica.Cache.IsTenantLoaded(Tenant));
        Assert.Throws<CkMethodDefinitionCacheNotLoadedException>(() =>
            new CkMethodRegistrationValidator(replica.Source()).Validate(Registrations()));
    }
}
