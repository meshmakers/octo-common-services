using System.Reflection;
using System.Text.Json;
using FakeItEasy;
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
///     Registry diagnostics (AB#7836): source and state of every row, health of the snapshot, in-process methods, and
///     service data only in the result.
/// </summary>
public class CkMethodHandlerRegistryDiagnosticsTests
{
    private const string Method = "System.Identity/User.ChangePassword-1";
    private const string OtherMethod = "System.Identity/User.ResetPassword-1";
    private const string ConnectionSecret = "mongodb://admin:hunter2@db:27017";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
    private readonly StubStore _store = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodHandlerRegistry NewRegistry() => new(_time);

    private CkMethodHandlerSnapshotPersistence Persistence(CkMethodHandlerRegistry registry, bool withStore = true,
        bool enabled = true) =>
        new(registry, Options.Create(new CkMethodDispatcherOptions { SnapshotEnabled = enabled }),
            NullLogger<CkMethodHandlerSnapshotPersistence>.Instance, _time, withStore ? _store : null);

    private CkMethodHandlerRegistryDiagnosticsService Diagnostics(CkMethodHandlerRegistry registry,
        CkMethodHandlerSnapshotPersistence persistence, ICkInProcessMethodTransport? inProcess = null) =>
        new(registry, persistence, _time, inProcess);

    private CkMethodHandlerAnnounced Announce(string instance = "a", string method = Method, int ttl = 45,
        DateTime? at = null) =>
        new(Guid.NewGuid(), at ?? Now)
        {
            ServiceName = "identity",
            InstanceId = instance,
            MethodIds = [method],
            TtlSeconds = ttl,
            ContractVersion = 1,
            Tenants = [CkMethodHandlerTenants.All]
        };

    private CkMethodHandlerWithdrawn Withdraw(string instance = "a", string method = Method, DateTime? at = null) =>
        new(Guid.NewGuid(), at ?? Now) { ServiceName = "identity", InstanceId = instance, MethodIds = [method] };

    private static CkMethodHandlerSnapshotRecord Row(string instance, DateTime lastSeen, DateTime expires,
        bool withdrawn = false) =>
        new()
        {
            MethodKey = CkMethodHandlerRegistry.Normalize(Method),
            InstanceId = instance,
            MethodId = Method,
            ServiceName = "identity",
            ContractVersion = 1,
            LastSeenUtc = lastSeen,
            ExpiresUtc = expires,
            Withdrawn = withdrawn,
            WithdrawnAtSource = withdrawn ? lastSeen : null,
            Tenants = ["*"]
        };

    [Fact]
    public async Task RestoredRow_HasSourceSnapshot_UntilAnAnnouncementOfTheSameInstanceReplacesIt()
    {
        var registry = NewRegistry();
        _store.Seed(Row("a", Now.AddSeconds(-5), Now.AddSeconds(40)));
        await Persistence(registry).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodHandlerEntrySource.Snapshot, Assert.Single(registry.Snapshot()).Source);
        Assert.Equal(CkMethodHandlerEntrySource.Snapshot, Assert.Single(registry.GetLiveHandlers(Method)).Source);

        _time.Advance(TimeSpan.FromSeconds(3));
        registry.OnAnnounced(Announce("b")); // another instance does not confirm "a"
        Assert.Equal(CkMethodHandlerEntrySource.Snapshot, registry.Snapshot().Single(e => e.InstanceId == "a").Source);

        registry.OnAnnounced(Announce("a")); // heartbeat or discovery answer: both arrive as an announcement
        Assert.All(registry.Snapshot(), e => Assert.Equal(CkMethodHandlerEntrySource.Announcement, e.Source));
    }

    [Fact]
    public async Task WithdrawalOfARestoredRow_IsAReceivedMessage_SourceAnnouncement()
    {
        var registry = NewRegistry();
        _store.Seed(Row("a", Now.AddSeconds(-5), Now.AddSeconds(40)));
        await Persistence(registry).LoadAsync(TestContext.Current.CancellationToken);

        registry.OnWithdrawn(Withdraw("a", at: Now.AddSeconds(1)));

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(CkMethodHandlerEntrySource.Announcement, entry.Source);
        Assert.Equal(CkMethodHandlerState.Withdrawn, entry.GetState(Now));
    }

    [Fact]
    public void ReceivedAnnouncement_IsNeverASnapshotRow()
    {
        var registry = NewRegistry();
        registry.OnAnnounced(Announce());

        Assert.Equal(CkMethodHandlerEntrySource.Announcement, Assert.Single(registry.Snapshot()).Source);
    }

    [Fact]
    public void State_IsLiveExpiredWithdrawnAndNoQueueBound_AndAgreesWithGetLiveHandlers()
    {
        var registry = NewRegistry();
        registry.OnAnnounced(Announce("live"));
        registry.OnAnnounced(Announce("expired", ttl: 1));
        registry.OnAnnounced(Announce("withdrawn"));
        registry.OnWithdrawn(Withdraw("withdrawn", at: Now.AddSeconds(1)));
        registry.OnAnnounced(Announce("nobroker", OtherMethod));
        _time.Advance(TimeSpan.FromSeconds(2));
        registry.OnAnnounced(Announce("live")); // keeps "live" inside its window after the time moved
        registry.MarkNoQueueBound(OtherMethod, Now);

        var now = Now;
        var states = registry.Snapshot().ToDictionary(e => e.InstanceId, e => e.GetState(now));
        Assert.Equal(CkMethodHandlerState.Live, states["live"]);
        Assert.Equal(CkMethodHandlerState.Expired, states["expired"]);
        Assert.Equal(CkMethodHandlerState.Withdrawn, states["withdrawn"]);
        Assert.Equal(CkMethodHandlerState.NoQueueBound, states["nobroker"]);

        foreach (var method in new[] { Method, OtherMethod })
        {
            var live = registry.GetLiveHandlers(method).Select(e => e.InstanceId).ToHashSet();
            foreach (var entry in registry.Snapshot().Where(e => CkMethodHandlerRegistry.Normalize(e.MethodId) ==
                                                                  CkMethodHandlerRegistry.Normalize(method)))
            {
                Assert.Equal(live.Contains(entry.InstanceId), entry.GetState(now) == CkMethodHandlerState.Live);
            }
        }
    }

    [Fact]
    public async Task RestoredRow_NeverReportsLiveAfterItsExpiry_LastSeenIsNotLive()
    {
        var registry = NewRegistry();
        _store.Seed(Row("a", Now.AddSeconds(-5), Now.AddSeconds(10)));
        _store.Seed(Row("w", Now.AddSeconds(-5), Now.AddSeconds(-5), withdrawn: true));
        var persistence = Persistence(registry);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        var diagnostics = Diagnostics(registry, persistence);

        var before = diagnostics.GetDiagnostics().Entries;
        Assert.Equal(CkMethodHandlerState.Live, before.Single(e => e.InstanceId == "a").State);
        Assert.Equal(CkMethodHandlerState.Withdrawn, before.Single(e => e.InstanceId == "w").State);

        _time.Advance(TimeSpan.FromSeconds(10)); // exactly at ExpiresUtc: not live any more
        var after = diagnostics.GetDiagnostics().Entries;
        var restored = after.Single(e => e.InstanceId == "a");
        Assert.Equal(CkMethodHandlerState.Expired, restored.State);
        Assert.Equal(CkMethodHandlerEntrySource.Snapshot, restored.Source);
        Assert.Empty(registry.GetLiveHandlers(Method));
    }

    [Fact]
    public async Task Snapshot_ReportsLoadAndFlush()
    {
        var registry = NewRegistry();
        _store.Seed(Row("a", Now.AddSeconds(-5), Now.AddSeconds(40)));
        _store.Seed(Row("old", Now.AddHours(-30), Now.AddHours(-30))); // skipped by the retention
        var persistence = Persistence(registry);
        var diagnostics = Diagnostics(registry, persistence);

        var initial = diagnostics.GetDiagnostics().Snapshot;
        Assert.True(initial.Enabled);
        Assert.Null(initial.Load);
        Assert.Null(initial.Flush.LastSuccessUtc);
        Assert.False(initial.Flush.LastFailed);

        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce("b"));
        _time.Advance(TimeSpan.FromSeconds(5));
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        var snapshot = diagnostics.GetDiagnostics().Snapshot;
        Assert.Equal(new CkMethodHandlerSnapshotLoadStatus(Now.AddSeconds(-5), 2, 1, false, null), snapshot.Load);
        Assert.Equal(Now, snapshot.Flush.LastSuccessUtc);
        Assert.False(snapshot.Flush.LastFailed);
    }

    [Fact]
    public async Task FailingStore_YieldsFailedFlagAndTypeName_NeverTheMessage()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry);
        var diagnostics = Diagnostics(registry, persistence);
        _store.Failure = new InvalidOperationException(ConnectionSecret);

        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        var result = diagnostics.GetDiagnostics();
        Assert.True(result.Snapshot.Load!.Failed);
        Assert.Equal(nameof(InvalidOperationException), result.Snapshot.Load.FailureExceptionType);
        Assert.True(result.Snapshot.Flush.LastFailed);
        Assert.Equal(nameof(InvalidOperationException), result.Snapshot.Flush.LastFailureExceptionType);
        Assert.NotNull(result.Snapshot.Flush.LastFailureUtc);
        Assert.Null(result.Snapshot.Flush.LastSuccessUtc);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain(ConnectionSecret, json);

        // a later successful flush clears the flag and keeps the failure time as history
        _store.Failure = null;
        await persistence.FlushAsync(TestContext.Current.CancellationToken);
        var recovered = diagnostics.GetDiagnostics().Snapshot.Flush;
        Assert.False(recovered.LastFailed);
        Assert.NotNull(recovered.LastSuccessUtc);
    }

    [Fact]
    public async Task WithoutAStore_SnapshotIsDisabled_AndTheRegistryBehavesAsBefore()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, withStore: false);
        await persistence.LoadAsync(TestContext.Current.CancellationToken);
        registry.OnAnnounced(Announce());
        await persistence.FlushAsync(TestContext.Current.CancellationToken);

        var result = Diagnostics(registry, persistence).GetDiagnostics();

        Assert.False(result.Snapshot.Enabled);
        Assert.Null(result.Snapshot.Load);
        Assert.Null(result.Snapshot.Flush.LastSuccessUtc);
        Assert.Single(result.Entries);
        Assert.Single(registry.GetLiveHandlers(Method));
        Assert.Empty(registry.TakeChanges());
    }

    [Fact]
    public void SnapshotDisabledByOption_IsReportedDisabled()
    {
        var registry = NewRegistry();

        var result = Diagnostics(registry, Persistence(registry, enabled: false)).GetDiagnostics();

        Assert.False(result.Snapshot.Enabled);
    }

    [Fact]
    public void InProcessMethods_AreListedWithTransportInProcess_AndEmptyWithoutATransport()
    {
        var registry = NewRegistry();
        var persistence = Persistence(registry, withStore: false);
        var transport = A.Fake<ICkInProcessMethodTransport>();
        A.CallTo(() => transport.MethodIds).Returns([OtherMethod, Method]);

        var with = Diagnostics(registry, persistence, transport).GetDiagnostics();
        var without = Diagnostics(registry, persistence).GetDiagnostics();

        Assert.Equal([Method, OtherMethod], with.InProcessMethods.Select(m => m.MethodId));
        Assert.All(with.InProcessMethods, m => Assert.Equal(CkMethodTransportNames.InProcess, m.Transport));
        Assert.Empty(without.InProcessMethods);
    }

    [Fact]
    public void Entries_CarryTransportBus_InstanceIdAndAreOrdered()
    {
        var registry = NewRegistry();
        registry.OnAnnounced(Announce("b"));
        registry.OnAnnounced(Announce("a"));

        var result = Diagnostics(registry, Persistence(registry, withStore: false)).GetDiagnostics();

        Assert.Equal(["a", "b"], result.Entries.Select(e => e.InstanceId));
        Assert.All(result.Entries, e => Assert.Equal(CkMethodTransportNames.Bus, e.Transport));
        Assert.Equal(CkMethodDispatcherStartupService.DispatcherInstanceId, result.DispatcherInstanceId);
        Assert.Equal(Now, result.GeneratedAtUtc);
    }

    [Fact]
    public void ResultTypes_HoldServiceDataOnly_NoParameterTokenOrUserData()
    {
        // an allow-list: a new field has to be added on purpose, after asking whether it is service data
        HashSet<string> allowed =
        [
            "DispatcherInstanceId", "GeneratedAtUtc", "Snapshot", "Entries", "InProcessMethods",
            "Enabled", "Load", "Flush",
            "LoadedAtUtc", "RowsRead", "RowsRestored", "Failed", "FailureExceptionType",
            "LastSuccessUtc", "LastFailed", "LastFailureUtc", "LastFailureExceptionType",
            "MethodId", "ServiceName", "InstanceId", "Version", "ContractVersion", "LastSeenUtc", "ExpiresUtc", "Tenants",
            "Source", "State", "Transport"
        ];
        Type[] types =
        [
            typeof(CkMethodHandlerRegistryDiagnostics), typeof(CkMethodHandlerSnapshotDiagnostics),
            typeof(CkMethodHandlerSnapshotLoadStatus), typeof(CkMethodHandlerSnapshotFlushStatus),
            typeof(CkMethodHandlerDiagnosticsEntry), typeof(CkMethodInProcessDiagnosticsEntry)
        ];

        var names = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.Name != "EqualityContract").Select(p => p.Name).ToList();

        Assert.All(names, n => Assert.Contains(n, allowed));
        Assert.DoesNotContain(names, n => n.Contains("Parameters") || n.Contains("Token") || n.Contains("Caller") ||
                                          n.Contains("Message") || n.Contains("User"));
    }

    [Fact]
    public void AddCkMethodDispatcher_RegistersTheDiagnostics_WithAndWithoutAnInProcessTransport()
    {
        var provider = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddCkMethodDispatcher().BuildServiceProvider();

        var result = provider.GetRequiredService<ICkMethodHandlerRegistryDiagnostics>().GetDiagnostics();

        Assert.False(result.Snapshot.Enabled);
        Assert.Empty(result.Entries);
        Assert.Empty(result.InProcessMethods);
    }

    private sealed class StubStore : ICkMethodHandlerSnapshotStore
    {
        private readonly Dictionary<(string, string), CkMethodHandlerSnapshotRecord> _rows = new();

        public Exception? Failure { get; set; }

        public void Seed(CkMethodHandlerSnapshotRecord record) => _rows[(record.MethodKey, record.InstanceId)] = record;

        public Task<IReadOnlyList<CkMethodHandlerSnapshotRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Failure != null
                ? throw Failure
                : Task.FromResult<IReadOnlyList<CkMethodHandlerSnapshotRecord>>(_rows.Values.ToList());

        public Task<bool> UpsertAsync(IReadOnlyCollection<CkMethodHandlerSnapshotRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (Failure != null)
            {
                throw Failure;
            }

            foreach (var record in records)
            {
                _rows[(record.MethodKey, record.InstanceId)] = record;
            }

            return Task.FromResult(true);
        }
    }
}
