using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

/// <summary>
///     The registry snapshot (AB#5671): a restarted host answers from the persisted rows, a stale or withdrawn row is
///     never live, the stored document is service data only and without a store nothing changes.
/// </summary>
public class CkMethodHandlerSnapshotTests
{
    private const string Method = "System.Identity/User.ChangePassword-1";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemorySnapshotStore _store = new();
    private readonly IDistributionEventHubService _eventHub = A.Fake<IDistributionEventHubService>();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodHandlerRegistry NewRegistry() => new(_time);

    private CkMethodHandlerSnapshotPersistence Persistence(CkMethodHandlerRegistry registry,
        ICkMethodHandlerSnapshotStore? store, bool enabled = true) =>
        new(registry, Options.Create(new CkMethodDispatcherOptions { SnapshotEnabled = enabled }),
            NullLogger<CkMethodHandlerSnapshotPersistence>.Instance, store);

    private CkMethodHandlerAnnounced Announce(string instance = "a", int ttl = 45, IReadOnlyList<string>? tenants = null,
        DateTime? at = null, int contractVersion = 1) =>
        new(Guid.NewGuid(), at ?? Now)
        {
            ServiceName = "identity",
            InstanceId = instance,
            MethodIds = [Method],
            TtlSeconds = ttl,
            ContractVersion = contractVersion,
            Tenants = tenants ?? [CkMethodHandlerTenants.All]
        };

    private CkMethodHandlerWithdrawn Withdraw(string instance = "a", DateTime? at = null) =>
        new(Guid.NewGuid(), at ?? Now) { ServiceName = "identity", InstanceId = instance, MethodIds = [Method] };

    /// <summary>A host that runs, receives announcements and flushes, then "restarts" with a fresh registry.</summary>
    private async Task<(CkMethodHandlerRegistry Restarted, CkMethodHandlerSnapshotPersistence Persistence)> RestartAfter(
        Action<CkMethodHandlerRegistry> beforeRestart, TimeSpan downtime = default)
    {
        var first = NewRegistry();
        var firstPersistence = Persistence(first, _store);
        await firstPersistence.LoadAsync(TestContext.Current.CancellationToken);
        beforeRestart(first);
        await firstPersistence.FlushAsync(TestContext.Current.CancellationToken);

        _time.Advance(downtime);
        var restarted = NewRegistry();
        var persistence = Persistence(restarted, _store);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        return (restarted, persistence);
    }

    private CkMethodDispatcher Dispatcher(CkMethodHandlerRegistry registry)
    {
        var resolver = A.Fake<ICkMethodDeclaringElementResolver>();
        A.CallTo(() => resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
        var pipeline = new CkMethodInvocationPipeline([], new CkMethodSensitivityResolver(null),
            NullLogger<CkMethodInvocationPipeline>.Instance);
        return new CkMethodDispatcher(resolver, pipeline, registry, _eventHub, new ServiceCollection().BuildServiceProvider(),
            new CkMethodSensitivityResolver(null), Options.Create(new CkMethodDispatcherOptions()), _time,
            NullLogger<CkMethodDispatcher>.Instance);
    }

    private CkMethodRequest Request(string tenant = "t1") =>
        TestData.Request(Now.AddSeconds(15), tenant);

    private int Sends() => Fake.GetCalls(_eventHub).Count(c => c.Method.Name == "GetCommandResponseAsync");

    [Fact]
    public async Task AliveHandler_IsLiveAfterRestart_AndCallIsDispatchedWithoutAHeartbeat()
    {
        var (restarted, _) = await RestartAfter(r => r.OnAnnounced(Announce(tenants: ["t1"])),
            downtime: TimeSpan.FromSeconds(10));

        // no announcement since the restart: the row of the snapshot is inside its TTL window
        var live = Assert.Single(restarted.GetLiveHandlers(Method));
        Assert.Equal("a", live.InstanceId);

        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._))
            .Returns(CkMethodResponse.Succeeded(Guid.Empty, "a"));
        var response = await Dispatcher(restarted).InvokeAsync(Request("t1"), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task RestoredRow_DrivesTheTenantAndContractChecks_BeforeAnyHeartbeat()
    {
        var (restarted, _) = await RestartAfter(r => r.OnAnnounced(Announce(tenants: ["other"], contractVersion: 1)),
            downtime: TimeSpan.FromSeconds(5));

        var response = await Dispatcher(restarted).InvokeAsync(Request("t1"), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal("tenant-not-served", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task StoppedHandler_IsOnlyLastSeenAfterRestart_AndTheCallEndsNotAvailable()
    {
        var (restarted, _) = await RestartAfter(r => r.OnAnnounced(Announce(ttl: 45)),
            downtime: TimeSpan.FromSeconds(120));

        Assert.Empty(restarted.GetLiveHandlers(Method));
        Assert.Equal("a", restarted.GetLastSeen(Method)!.InstanceId);

        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._))
            .Throws(new CommandNotRoutableException());
        var response = await Dispatcher(restarted).InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal("no-live-handler", response.Error.Parameters[CkMethodErrorParameters.Reason]);
    }

    [Fact]
    public async Task WithdrawnHandler_LeavesNoLiveRow_AndALateAnnouncementCannotResurrectIt()
    {
        var announced = Announce();
        var withdrawal = Withdraw(at: announced.Timestamp.AddSeconds(1));
        var (restarted, _) = await RestartAfter(r =>
        {
            r.OnAnnounced(announced);
            r.OnWithdrawn(withdrawal);
        });

        Assert.Empty(restarted.GetLiveHandlers(Method));
        Assert.True(restarted.GetLastSeen(Method)!.Withdrawn);

        restarted.OnAnnounced(announced); // delivered after the restart, older than the withdrawal
        Assert.Empty(restarted.GetLiveHandlers(Method));
        restarted.OnAnnounced(Announce(at: Now.AddSeconds(30))); // a real restart of the handler is newer
        Assert.Single(restarted.GetLiveHandlers(Method));
    }

    [Fact]
    public async Task ReceivedAnnouncement_WinsOverTheSnapshotRow()
    {
        var first = NewRegistry();
        var firstPersistence = Persistence(first, _store);
        await firstPersistence.LoadAsync(TestContext.Current.CancellationToken);
        first.OnAnnounced(Announce(tenants: ["old"]));
        await firstPersistence.FlushAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(5));
        var restarted = NewRegistry();
        restarted.OnAnnounced(Announce(tenants: ["new"])); // the discovery answer is faster than the load
        await Persistence(restarted, _store).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["new"], Assert.Single(restarted.GetLiveHandlers(Method)).Tenants);
    }

    [Fact]
    public async Task Restore_BoundsTheTimes_ARowCannotLiveLongerThanAnAnnouncementCould()
    {
        var restarted = NewRegistry();
        _store.Seed(new CkMethodHandlerSnapshotRecord
        {
            MethodKey = CkMethodHandlerRegistry.Normalize(Method),
            InstanceId = "x",
            MethodId = Method,
            ServiceName = "identity",
            ContractVersion = 1,
            LastSeenUtc = Now.AddDays(30), // from the future
            ExpiresUtc = Now.AddDays(3650),
            Tenants = ["*"]
        });

        await Persistence(restarted, _store).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(restarted.GetLiveHandlers(Method));
        _time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Empty(restarted.GetLiveHandlers(Method));
    }

    [Fact]
    public async Task Restore_SkipsRowsOlderThanTheRetention()
    {
        var restarted = NewRegistry();
        _store.Seed(new CkMethodHandlerSnapshotRecord
        {
            MethodKey = CkMethodHandlerRegistry.Normalize(Method),
            InstanceId = "old",
            MethodId = Method,
            ServiceName = "identity",
            LastSeenUtc = Now.AddHours(-25),
            ExpiresUtc = Now.AddHours(-25),
            Tenants = ["*"]
        });

        await Persistence(restarted, _store).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(restarted.GetLastSeen(Method));
    }

    [Fact]
    public async Task StoredRows_AreServiceDataOnly_NoParameterTokenOrUserData()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, _store);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());
        registry.OnWithdrawn(Withdraw("b"));
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        var row = _store.Rows.First();
        var json = JsonSerializer.Serialize(row);
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // an allow-list: a new field has to be added on purpose, after asking whether it is service data
        HashSet<string> allowed =
        [
            "MethodKey", "InstanceId", "MethodId", "ServiceName", "Version", "ContractVersion", "LastSeenUtc", "ExpiresUtc",
            "Withdrawn", "WithdrawnAtSource", "NoQueueBound", "Tenants", "RetainUntilUtc"
        ];
        Assert.All(names, n => Assert.Contains(n, allowed));
        // the request side never reaches the registry: no parameters, caller token or caller identity on the row
        Assert.DoesNotContain(names, n => n.Contains("Parameters") || n.Contains("Token") || n.Contains("Caller"));
        Assert.DoesNotContain(TestData.Secret, json);
        Assert.DoesNotContain(TestData.Token, json);
    }

    [Fact]
    public async Task WithoutAStore_NothingIsQueued_AndTheRegistryBehavesAsBefore()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, store: null);

        Assert.False(persistence.IsEnabled);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Empty(registry.TakeChanges());
        Assert.Single(registry.GetLiveHandlers(Method));
        Assert.Equal(0, _store.UpsertCalls);
    }

    [Fact]
    public async Task SnapshotDisabledByOption_DoesNotTouchTheStore()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, _store, enabled: false);

        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, _store.LoadCalls + _store.UpsertCalls);
    }

    [Fact]
    public async Task FailedOrDeferredWrite_IsRetriedWithTheNextFlush()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, _store);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());

        _store.Deferred = true; // system tenant not there yet
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_store.Rows);

        _store.Deferred = false;
        _store.Fail = true;
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_store.Rows);

        _store.Fail = false;
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Single(_store.Rows);

        // nothing changed since: the next flush writes nothing
        var calls = _store.UpsertCalls;
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(calls, _store.UpsertCalls);
    }

    [Fact]
    public async Task FailingLoad_IsNotFatal_TheRegistryFillsFromHeartbeats()
    {
        var registry = NewRegistry();
        _store.Fail = true;
        var persistence = Persistence(registry, _store);

        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());

        Assert.Single(registry.GetLiveHandlers(Method));
    }

    [Fact]
    public void AddCkMethodDispatcher_UsesTheStoreWhenTheHostRegistersOne()
    {
        var withoutStore = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddCkMethodDispatcher().BuildServiceProvider();
        Assert.False(withoutStore.GetRequiredService<CkMethodHandlerSnapshotPersistence>().IsEnabled);

        var withStore = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<ICkMethodHandlerSnapshotStore>(_store)
            .AddCkMethodDispatcher().BuildServiceProvider();
        Assert.True(withStore.GetRequiredService<CkMethodHandlerSnapshotPersistence>().IsEnabled);
    }

    private sealed class InMemorySnapshotStore : ICkMethodHandlerSnapshotStore
    {
        private readonly Dictionary<(string, string), CkMethodHandlerSnapshotRecord> _rows = new();

        public bool Fail { get; set; }
        public bool Deferred { get; set; }
        public int LoadCalls { get; private set; }
        public int UpsertCalls { get; private set; }

        public IReadOnlyCollection<CkMethodHandlerSnapshotRecord> Rows => _rows.Values.ToList();

        public void Seed(CkMethodHandlerSnapshotRecord record) => _rows[(record.MethodKey, record.InstanceId)] = record;

        public Task<IReadOnlyList<CkMethodHandlerSnapshotRecord>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            if (Fail)
            {
                throw new InvalidOperationException("store down");
            }

            return Task.FromResult<IReadOnlyList<CkMethodHandlerSnapshotRecord>>(_rows.Values.ToList());
        }

        public Task<bool> UpsertAsync(IReadOnlyCollection<CkMethodHandlerSnapshotRecord> records,
            CancellationToken cancellationToken = default)
        {
            UpsertCalls++;
            if (Fail)
            {
                throw new InvalidOperationException("store down");
            }

            if (Deferred)
            {
                return Task.FromResult(false);
            }

            foreach (var record in records)
            {
                var key = (record.MethodKey, record.InstanceId);
                if (!_rows.TryGetValue(key, out var existing) || existing.LastSeenUtc < record.LastSeenUtc)
                {
                    _rows[key] = record;
                }
            }

            return Task.FromResult(true);
        }
    }
}
