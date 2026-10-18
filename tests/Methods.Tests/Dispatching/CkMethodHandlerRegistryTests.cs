using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

public class CkMethodHandlerRegistryTests
{
    private const string MethodId = "System.Identity/User.ChangePassword-1";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero));
    private readonly CkMethodHandlerRegistry _registry;

    public CkMethodHandlerRegistryTests()
    {
        _registry = new CkMethodHandlerRegistry(_time);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodHandlerAnnounced Announce(string instance, int ttlSeconds = 45, DateTime? at = null,
        IReadOnlyList<string>? tenants = null, int contractVersion = 1, params string[] methods) =>
        new(Guid.NewGuid(), at ?? Now)
        {
            ServiceName = "identity",
            InstanceId = instance,
            MethodIds = methods.Length == 0 ? [MethodId] : methods,
            TtlSeconds = ttlSeconds,
            ContractVersion = contractVersion,
            Tenants = tenants ?? [CkMethodHandlerTenants.All]
        };

    private CkMethodHandlerWithdrawn Withdraw(string instance, DateTime? at = null) =>
        new(Guid.NewGuid(), at ?? Now) { ServiceName = "identity", InstanceId = instance, MethodIds = [MethodId] };

    [Fact]
    public void Announced_InstanceIsLive_UntilTtlWithoutHeartbeat()
    {
        _registry.OnAnnounced(Announce("a", ttlSeconds: 45));

        Assert.Single(_registry.GetLiveHandlers(MethodId));
        _time.Advance(TimeSpan.FromSeconds(44));
        Assert.Single(_registry.GetLiveHandlers(MethodId));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(_registry.GetLiveHandlers(MethodId));
        Assert.NotNull(_registry.GetLastSeen(MethodId));
    }

    [Fact]
    public void Heartbeat_ExtendsTheEntry()
    {
        _registry.OnAnnounced(Announce("a", ttlSeconds: 45));
        _time.Advance(TimeSpan.FromSeconds(30));
        _registry.OnAnnounced(Announce("a", ttlSeconds: 45));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Single(_registry.GetLiveHandlers(MethodId));
    }

    [Fact]
    public void Expiry_UsesTheDispatcherClock_NotTheHandlerTimestamp()
    {
        // the handler's clock is 10 minutes behind: the entry must still be live for a full TTL after receipt
        _registry.OnAnnounced(Announce("a", ttlSeconds: 45, at: Now.AddMinutes(-10)));

        Assert.Single(_registry.GetLiveHandlers(MethodId));
    }

    [Fact]
    public void Withdrawn_InstanceIsGone_OtherInstanceStays()
    {
        _registry.OnAnnounced(Announce("a"));
        _registry.OnAnnounced(Announce("b"));

        _registry.OnWithdrawn(Withdraw("a"));

        Assert.Equal(["b"], _registry.GetLiveHandlers(MethodId).Select(e => e.InstanceId));
    }

    [Fact]
    public void LateHeartbeat_AfterWithdrawal_DoesNotResurrectTheInstance()
    {
        var announcedAt = Now;
        _registry.OnAnnounced(Announce("a", at: announcedAt));
        _registry.OnWithdrawn(Withdraw("a", at: announcedAt.AddSeconds(5)));

        // delivered after the withdrawal, but written before it (the N8 race of the discovery answer)
        _registry.OnAnnounced(Announce("a", at: announcedAt.AddSeconds(4)));

        Assert.Empty(_registry.GetLiveHandlers(MethodId));
        Assert.True(_registry.GetLastSeen(MethodId)!.Withdrawn);
    }

    [Fact]
    public void WithdrawalBeforeFirstAnnouncement_StillBlocksTheOlderAnnouncement()
    {
        var at = Now;
        _registry.OnWithdrawn(Withdraw("a", at: at.AddSeconds(5)));
        _registry.OnAnnounced(Announce("a", at: at));

        Assert.Empty(_registry.GetLiveHandlers(MethodId));
    }

    [Fact]
    public void AnnouncementNewerThanTheWithdrawal_IsAccepted()
    {
        var at = Now;
        _registry.OnWithdrawn(Withdraw("a", at: at));
        _registry.OnAnnounced(Announce("a", at: at.AddSeconds(1)));

        Assert.Single(_registry.GetLiveHandlers(MethodId));
    }

    [Fact]
    public void OlderWithdrawal_DoesNotLowerTheWatermark()
    {
        var at = Now;
        _registry.OnWithdrawn(Withdraw("a", at: at.AddSeconds(10)));
        _registry.OnWithdrawn(Withdraw("a", at: at.AddSeconds(2)));
        _registry.OnAnnounced(Announce("a", at: at.AddSeconds(5)));

        Assert.Empty(_registry.GetLiveHandlers(MethodId));
    }

    [Fact]
    public void NoQueueBound_ExpiresEntriesSeenBeforeTheSend_ButNotOnesAnnouncedDuringIt()
    {
        _registry.OnAnnounced(Announce("old"));
        _time.Advance(TimeSpan.FromSeconds(1));
        var sendStarted = Now;
        _time.Advance(TimeSpan.FromMilliseconds(50));
        _registry.OnAnnounced(Announce("new")); // rolling update: announced while the send was in flight

        _registry.MarkNoQueueBound(MethodId, sendStarted);

        Assert.Equal(["new"], _registry.GetLiveHandlers(MethodId).Select(e => e.InstanceId));
        Assert.True(_registry.Snapshot().Single(e => e.InstanceId == "old").NoQueueBound);
    }

    [Fact]
    public void Lookup_IsModelVersionLessAndCaseInsensitive()
    {
        _registry.OnAnnounced(Announce("a"));

        Assert.Single(_registry.GetLiveHandlers("System.Identity-2.90.0/User.ChangePassword-1"));
        Assert.Single(_registry.GetLiveHandlers("system.identity/user.changepassword-1"));
        Assert.Empty(_registry.GetLiveHandlers("System.Identity/User.Other-1"));
    }

    [Fact]
    public void UnknownMethod_ReturnsEmpty_AndMalformedIdDoesNotThrow()
    {
        Assert.Empty(_registry.GetLiveHandlers("nonsense"));
        Assert.Null(_registry.GetLastSeen("nonsense"));
    }

    [Fact]
    public void ContractVersionAndTenants_AreKept()
    {
        _registry.OnAnnounced(Announce("a", contractVersion: 2, tenants: ["t1"]));

        var entry = Assert.Single(_registry.GetLiveHandlers(MethodId));
        Assert.Equal(2, entry.ContractVersion);
        Assert.True(CkMethodHandlerRegistry.Serves(entry, "t1"));
        Assert.False(CkMethodHandlerRegistry.Serves(entry, "t2"));
    }

    [Fact]
    public void LastSeen_IsPrunedAfter24Hours()
    {
        _registry.OnAnnounced(Announce("a"));
        _time.Advance(TimeSpan.FromHours(25));
        _registry.OnAnnounced(Announce("b", methods: "Other.Model/Thing.Do-1"));

        Assert.Null(_registry.GetLastSeen(MethodId));
        Assert.Single(_registry.Snapshot());
    }

    [Fact]
    public async Task ConcurrentAnnounceWithdrawAndLookup_NeverThrows_AndWithdrawalWins()
    {
        var at = Now;
        _registry.OnWithdrawn(Withdraw("victim", at: at.AddMinutes(1)));

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 500; n++)
            {
                _registry.OnAnnounced(Announce("victim", at: at)); // older than the withdrawal
                _registry.OnAnnounced(Announce($"i{i}"));
                _registry.GetLiveHandlers(MethodId);
                _registry.MarkNoQueueBound(MethodId, Now);
                _registry.Snapshot();
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.DoesNotContain(_registry.GetLiveHandlers(MethodId), e => e.InstanceId == "victim");
        Assert.True(_registry.Snapshot().Single(e => e.InstanceId == "victim").Withdrawn);
    }
}
