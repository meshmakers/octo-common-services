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

        public RetryTestCreator(ITenantSetupRetryStore? store, Func<string, Task>? setupTenant = null)
            : base(NullLogger<DefaultConfigurationCreatorServiceBase>.Instance, tenantSetupRetryStore: store)
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
