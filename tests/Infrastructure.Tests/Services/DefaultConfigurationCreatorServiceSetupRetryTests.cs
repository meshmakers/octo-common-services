using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#4690 — durable retry of a failed tenant setup. Before this, a setup that threw was logged and
///     forgotten: services on the base creator had no retry at all, so a tenant whose setup failed once
///     (e.g. its database was briefly unreachable right after a delete + recreate under the same name)
///     stayed half-provisioned until the pod restarted. Identity is the painful case — it owns the
///     roles/groups seed, so without it no administrator can be provisioned for that tenant.
/// </summary>
public class DefaultConfigurationCreatorServiceSetupRetryTests
{
    private const string ServiceId = "test-service";

    private readonly ITenantSetupRetryStore _store = A.Fake<ITenantSetupRetryStore>();

    [Fact]
    public async Task FailedSetup_IsRecordedDurably_AndRethrown()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var sut = new RetryTestCreator(_store, _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetupAsync(tenantId));

        A.CallTo(() => _store.RecordFailureAsync(ServiceId, tenantId, "boom", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.ClearAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task SuccessfulSetup_ClearsThePendingEntry()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var sut = new RetryTestCreator(_store);

        await sut.SetupAsync(tenantId);

        A.CallTo(() => _store.ClearAsync(ServiceId, tenantId, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.RecordFailureAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task SetupThatWasSkippedBecauseItIsAlreadyInWork_TouchesTheStoreOnlyOnce()
    {
        // The in-flight guard makes a concurrent SetupAsync return immediately. That path must not report
        // success to the retry store — otherwise a pending entry would be cleared by a call that never ran
        // the setup.
        var tenantId = $"t-{Guid.NewGuid():N}";
        var blocked = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var sut = new RetryTestCreator(_store, async _ =>
        {
            entered.SetResult();
            await blocked.Task;
        });

        var first = sut.SetupAsync(tenantId);
        await entered.Task;

        await sut.SetupAsync(tenantId); // skipped: already in work

        blocked.SetResult();
        await first;

        Assert.Single(sut.SetupCalls);
        A.CallTo(() => _store.ClearAsync(ServiceId, tenantId, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StoreFailures_DoNotChangeTheOutcomeOfSetup()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        A.CallTo(() => _store.ClearAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("store down"));
        var sut = new RetryTestCreator(_store);

        // The retry queue is recovery metadata, not part of the setup transaction.
        await sut.SetupAsync(tenantId);

        Assert.Single(sut.SetupCalls);
    }

    [Fact]
    public async Task RetryFailedTenants_DrainsTheQueue_UntilNothingIsDue()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        A.CallTo(() => _store.TryClaimAsync(ServiceId, A<string>._, A<TimeSpan>._, A<TimeSpan>._, A<int>._,
                A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new TenantSetupRetryRecord { ServiceId = ServiceId, TenantId = tenantId, AttemptCount = 1 },
                null);
        var sut = new RetryTestCreator(_store);

        await sut.RetryFailedTenantsAsync();

        Assert.Equal([tenantId], sut.SetupCalls);
        A.CallTo(() => _store.ClearAsync(ServiceId, tenantId, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RetryFailedTenants_KeepsGoing_WhenARetryFailsAgain()
    {
        var failing = $"t-{Guid.NewGuid():N}";
        var succeeding = $"t-{Guid.NewGuid():N}";
        A.CallTo(() => _store.TryClaimAsync(ServiceId, A<string>._, A<TimeSpan>._, A<TimeSpan>._, A<int>._,
                A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new TenantSetupRetryRecord { ServiceId = ServiceId, TenantId = failing, AttemptCount = 2 },
                new TenantSetupRetryRecord { ServiceId = ServiceId, TenantId = succeeding, AttemptCount = 1 },
                null);
        var sut = new RetryTestCreator(_store,
            tenantId => tenantId == failing ? throw new InvalidOperationException("still broken") : Task.CompletedTask);

        // One tenant that is still broken must not abort the drain for the others.
        await sut.RetryFailedTenantsAsync();

        Assert.Equal([failing, succeeding], sut.SetupCalls);
        A.CallTo(() => _store.RecordFailureAsync(ServiceId, failing, "still broken", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _store.ClearAsync(ServiceId, succeeding, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    ///     AB#4829. A pending entry whose tenant is gone from the registry can never be driven to
    ///     completion — every retry threw "does not exist", re-recorded the entry, and hammered the
    ///     just-deleted tenant every 60s until the attempt budget was exhausted, leaving a dead row.
    ///     The drain loop must recognize the terminal registry miss and drop the entry instead. On the
    ///     retry path this cannot collide with the PosCreateTenant uncommitted-record race: the first
    ///     claim happens at least MinSetupRetryInterval after the failure was recorded, long after any
    ///     legitimate create transaction has committed.
    /// </summary>
    [Fact]
    public async Task RetryFailedTenants_DropsTheEntry_WhenTheTenantNoLongerExists()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        A.CallTo(() => _store.TryClaimAsync(ServiceId, A<string>._, A<TimeSpan>._, A<TimeSpan>._, A<int>._,
                A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new TenantSetupRetryRecord { ServiceId = ServiceId, TenantId = tenantId, AttemptCount = 1 },
                null);
        var sut = new RetryTestCreator(_store,
            id => throw Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantException.TenantDoesNotExist(id));

        await sut.RetryFailedTenantsAsync();

        // The entry is gone afterwards — not merely re-recorded for the next 60s round.
        A.CallTo(() => _store.ClearAsync(ServiceId, tenantId, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RetryFailedTenants_KeepsRetrying_OnOrdinaryFailures()
    {
        // The terminal treatment is reserved for the registry miss — an ordinary failure keeps its
        // durable entry so the 60s loop can drive the tenant to completion (AB#4690 semantics).
        var tenantId = $"t-{Guid.NewGuid():N}";
        A.CallTo(() => _store.TryClaimAsync(ServiceId, A<string>._, A<TimeSpan>._, A<TimeSpan>._, A<int>._,
                A<CancellationToken>._))
            .ReturnsNextFromSequence(
                new TenantSetupRetryRecord { ServiceId = ServiceId, TenantId = tenantId, AttemptCount = 1 },
                null);
        var sut = new RetryTestCreator(_store, _ => throw new InvalidOperationException("still broken"));

        await sut.RetryFailedTenantsAsync();

        A.CallTo(() => _store.ClearAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _store.RecordFailureAsync(ServiceId, tenantId, "still broken", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    /// <summary>
    ///     AB#4829. The delete marks the tenant Deleting (durable tombstone) before it starts tearing
    ///     things down. A setup triggered while that tombstone lives (a late PosUpdateTenant echo, the
    ///     startup loop, a retry claim) must not run — it would fail against the half-deleted tenant,
    ///     re-record a retry row the delete just cleared, and its CK import could resurrect the dropped
    ///     database as an empty shell.
    /// </summary>
    [Fact]
    public async Task Setup_IsSkipped_WhileTheTenantIsBeingDeleted()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var lifecycleStore = A.Fake<ITenantLifecycleStore>();
        A.CallTo(() => lifecycleStore.GetAsync(tenantId, A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = tenantId, State = TenantLifecycleState.Deleting });
        var sut = new RetryTestCreator(_store, lifecycleStore: lifecycleStore);

        await sut.SetupAsync(tenantId);

        Assert.Empty(sut.SetupCalls);
        // The retry queue is the delete's to clean; skipping must neither record nor clear.
        A.CallTo(() => _store.RecordFailureAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _store.ClearAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Setup_Runs_ForATenantWithoutADeletingTombstone()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var lifecycleStore = A.Fake<ITenantLifecycleStore>();
        A.CallTo(() => lifecycleStore.GetAsync(tenantId, A<CancellationToken>._))
            .Returns((TenantLifecycleRecord?)null);
        var sut = new RetryTestCreator(_store, lifecycleStore: lifecycleStore);

        await sut.SetupAsync(tenantId);

        Assert.Equal([tenantId], sut.SetupCalls);
    }

    [Fact]
    public async Task Setup_Proceeds_WhenTheLifecycleStoreIsUnavailable()
    {
        // The lifecycle gate is recovery metadata, not part of setup — a store outage must not stop
        // tenants from being provisioned (same contract as the retry store, AB#4690).
        var tenantId = $"t-{Guid.NewGuid():N}";
        var lifecycleStore = A.Fake<ITenantLifecycleStore>();
        A.CallTo(() => lifecycleStore.GetAsync(tenantId, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("store down"));
        var sut = new RetryTestCreator(_store, lifecycleStore: lifecycleStore);

        await sut.SetupAsync(tenantId);

        Assert.Equal([tenantId], sut.SetupCalls);
    }

    [Fact]
    public async Task WithoutAStore_EverythingIsANoOp()
    {
        // Services that do not wire the store keep their previous behaviour exactly.
        var sut = new RetryTestCreator(null);

        await sut.RetryFailedTenantsAsync();
        await sut.SetupAsync($"t-{Guid.NewGuid():N}");

        Assert.Single(sut.SetupCalls);
    }

    private sealed class RetryTestCreator : DefaultConfigurationCreatorServiceBase
    {
        private readonly Func<string, Task> _setupTenant;

        public RetryTestCreator(ITenantSetupRetryStore? store, Func<string, Task>? setupTenant = null,
            ITenantLifecycleStore? lifecycleStore = null)
            : base(NullLogger<DefaultConfigurationCreatorServiceBase>.Instance, tenantSetupRetryStore: store,
                tenantLifecycleStore: lifecycleStore)
        {
            _setupTenant = setupTenant ?? (_ => Task.CompletedTask);
        }

        public List<string> SetupCalls { get; } = [];

        protected override string ServiceId => DefaultConfigurationCreatorServiceSetupRetryTests.ServiceId;

        protected override Task SetupTenantAsync(string tenantId)
        {
            SetupCalls.Add(tenantId);
            return _setupTenant(tenantId);
        }
    }
}
