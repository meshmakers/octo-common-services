using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#4829 — the delete settle sweep. A tenant delete cannot stop events and setups already in
///     flight across the platform, so it leaves its Deleting tombstone in place and this sweep
///     completes the delete once the settle period has passed: it re-drops a database a late CK import
///     resurrected as an empty shell (which would otherwise permanently block its own name, AB#4762),
///     clears re-seeded setup-retry rows, and only then removes the tombstone. A tombstone whose
///     tenant is still fully registered marks a delete that died before its metadata commit — the
///     sweep rolls it back instead, making crashed deletes converge in both directions.
/// </summary>
public class DefaultConfigurationCreatorServiceDeletingSweepTests
{
    private const string TenantId = "swept-tenant";
    private const string DatabaseName = "swept-tenant-db";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantLifecycleStore _lifecycleStore = A.Fake<ITenantLifecycleStore>();
    private readonly ITenantSetupRetryStore _retryStore = A.Fake<ITenantSetupRetryStore>();
    private readonly Guid _correlationId = Guid.NewGuid();

    private SweepTestCreator CreateSut(bool withLifecycleStore = true) => new(
        _systemContext, withLifecycleStore ? _lifecycleStore : null, _retryStore);

    private void SetupTombstone(TimeSpan age, string? databaseName = DatabaseName)
    {
        A.CallTo(() => _lifecycleStore.ListAsync(A<CancellationToken>._))
            .Returns(new[]
            {
                new TenantLifecycleRecord
                {
                    TenantId = TenantId,
                    DatabaseName = databaseName,
                    CorrelationId = _correlationId,
                    State = TenantLifecycleState.Deleting,
                    LastTransitionUtc = DateTime.UtcNow - age,
                },
            });
    }

    [Fact]
    public async Task Sweep_CompletesASettledDelete_WhenNothingResurrected()
    {
        SetupTombstone(TimeSpan.FromMinutes(10));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns((ITenantContext?)null);
        A.CallTo(() => _systemContext.IsDatabaseExistingAsync(DatabaseName)).Returns(false);

        await CreateSut().RetryFailedTenantsAsync();

        A.CallTo(() => _systemContext.DropTenantDatabaseAsync(A<TenantDeletionHandle>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _retryStore.ClearAllForTenantAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _lifecycleStore.RemoveAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Sweep_DropsAResurrectedShell_BeforeRemovingTheTombstone()
    {
        SetupTombstone(TimeSpan.FromMinutes(10));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns((ITenantContext?)null);
        A.CallTo(() => _systemContext.IsDatabaseExistingAsync(DatabaseName)).Returns(true);
        A.CallTo(() => _systemContext.TryGetTenantIdByDatabaseNameAsync(DatabaseName)).Returns((string?)null);

        await CreateSut().RetryFailedTenantsAsync();

        A.CallTo(() => _systemContext.DropTenantDatabaseAsync(
                new TenantDeletionHandle(DatabaseName, _correlationId), TenantId))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _retryStore.ClearAllForTenantAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _lifecycleStore.RemoveAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Sweep_NeverDropsADatabase_AnotherTenantHasClaimed()
    {
        // The name can be legitimately re-claimed by a DIFFERENT tenant id once the old database is
        // gone. The old tenant's delete is still completed (rows + tombstone), but the database now
        // belongs to someone else.
        SetupTombstone(TimeSpan.FromMinutes(10));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns((ITenantContext?)null);
        A.CallTo(() => _systemContext.IsDatabaseExistingAsync(DatabaseName)).Returns(true);
        A.CallTo(() => _systemContext.TryGetTenantIdByDatabaseNameAsync(DatabaseName)).Returns("new-owner");

        await CreateSut().RetryFailedTenantsAsync();

        A.CallTo(() => _systemContext.DropTenantDatabaseAsync(A<TenantDeletionHandle>._, A<string>._))
            .MustNotHaveHappened();
        A.CallTo(() => _retryStore.ClearAllForTenantAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _lifecycleStore.RemoveAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
    }

    [Fact]
    public async Task Sweep_LeavesAFreshTombstoneAlone_UntilTheSettlePeriodPassed()
    {
        // In-flight events and setups get their settle window before the sweep declares the delete
        // complete; touching the tombstone earlier would re-open exactly the race it exists to close.
        SetupTombstone(TimeSpan.FromSeconds(10));

        await CreateSut().RetryFailedTenantsAsync();

        A.CallTo(() => _lifecycleStore.RemoveAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _retryStore.ClearAllForTenantAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _systemContext.DropTenantDatabaseAsync(A<TenantDeletionHandle>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Sweep_RollsTheTombstoneBack_WhenTheTenantIsStillRegistered()
    {
        // A Deleting record for a fully registered tenant means the delete died before its metadata
        // commit. Rolling the tombstone back restores the pre-delete state; the tenant's (possibly
        // legitimate) pending setup retries are left untouched.
        SetupTombstone(TimeSpan.FromMinutes(10));
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(TenantId)).Returns(A.Fake<ITenantContext>());

        await CreateSut().RetryFailedTenantsAsync();

        A.CallTo(() => _lifecycleStore.RemoveAsync(TenantId, A<CancellationToken>._)).MustHaveHappened();
        A.CallTo(() => _retryStore.ClearAllForTenantAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _systemContext.DropTenantDatabaseAsync(A<TenantDeletionHandle>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Sweep_IsANoOp_WithoutALifecycleStore()
    {
        await CreateSut(withLifecycleStore: false).RetryFailedTenantsAsync();

        A.CallTo(() => _retryStore.ClearAllForTenantAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private sealed class SweepTestCreator : DefaultConfigurationCreatorServiceStandardized
    {
        public SweepTestCreator(ISystemContext systemContext, ITenantLifecycleStore? lifecycleStore,
            ITenantSetupRetryStore retryStore)
            : base(
                NullLogger<DefaultConfigurationCreatorServiceStandardized>.Instance,
                systemContext,
                A.Fake<ICommandClient<CreateIdentityDataCommandRequest>>(),
                identityDataVersionKey: "test-id-data-version",
                expectedIdentityDataVersion: 1,
                tenantLifecycleStore: lifecycleStore,
                tenantSetupRetryStore: retryStore)
        {
        }

        protected override Task SetupTenantAsync(string tenantId) => Task.CompletedTask;
    }
}
