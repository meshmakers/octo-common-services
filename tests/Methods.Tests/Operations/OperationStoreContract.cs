using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>
///     AB#8443: the contract of <see cref="ICkMethodOperationStore" />, run against every implementation (in-memory here,
///     MongoDB in <c>Methods.IntegrationTests</c>, which links this file). It covers exactly what AB#8444 reads: one record
///     by tenant and operation id, and a paged list by tenant with owner, method, state and since filters.
/// </summary>
public abstract class OperationStoreContract
{
    protected const string TenantA = "tenanta";
    protected const string TenantB = "tenantb";
    protected const string MethodOne = "Test.Ops/Thing.Export-1";
    protected const string MethodTwo = "Test.Ops/Thing.Import-1";

    protected static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    protected DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>A fresh, empty store that reads the clock from <see cref="Time" />.</summary>
    protected abstract Task<ICkMethodOperationStore> CreateStoreAsync();

    protected CkMethodOperationRecord NewRecord(string tenantId = TenantA, string methodId = MethodOne,
        CkMethodOperationOwner? owner = null, DateTime? acceptedAt = null) =>
        new()
        {
            TenantId = tenantId,
            OperationId = Guid.NewGuid().ToString("N"),
            MethodId = methodId,
            InvocationId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Owner = owner ?? new CkMethodOperationOwner("user-1", null),
            HandlerInstanceId = "handler-1",
            AcceptedAtUtc = acceptedAt ?? Now,
            HeartbeatAtUtc = acceptedAt ?? Now
        };

    private CkMethodOperationCompletion Success(string? resultJson = "\"done\"") => new()
    {
        State = CkMethodOperationState.Succeeded,
        ResultJson = resultJson,
        CompletedAtUtc = Now,
        ExpiresAtUtc = Now + Retention
    };

    [Fact]
    public async Task Create_ThenGet_ReturnsTheRecordAsAccepted()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord(owner: new CkMethodOperationOwner(null, "svc-client"));

        await store.CreateAsync(record, TestContext.Current.CancellationToken);
        var read = await store.GetAsync(TenantA, record.OperationId, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(CkMethodOperationState.Accepted, read.State);
        Assert.Equal(record.MethodId, read.MethodId);
        Assert.Equal(record.InvocationId, read.InvocationId);
        Assert.Equal(record.CorrelationId, read.CorrelationId);
        Assert.Equal(record.Owner, read.Owner);
        Assert.Equal(record.HandlerInstanceId, read.HandlerInstanceId);
        Assert.Equal(record.AcceptedAtUtc, read.AcceptedAtUtc);
        Assert.Null(read.ExpiresAtUtc);
        Assert.Null(read.ResultJson);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task Create_TheSameIdTwiceInATenant_Throws()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord();
        await store.CreateAsync(record, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CkMethodOperationStoreException>(() =>
            store.CreateAsync(record with { InvocationId = Guid.NewGuid() }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadForTenantB_NeverReturnsARecordOfTenantA()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord(TenantA);
        await store.CreateAsync(record, TestContext.Current.CancellationToken);

        Assert.Null(await store.GetAsync(TenantB, record.OperationId, TestContext.Current.CancellationToken));
        var page = await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantB },
            TestContext.Current.CancellationToken);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.False(await store.HeartbeatAsync(TenantB, record.OperationId, Now, TestContext.Current.CancellationToken));
        Assert.False(await store.CompleteAsync(TenantB, record.OperationId, Success(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TenantIds_CompareCaseInsensitively()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord("TenantA");
        await store.CreateAsync(record, TestContext.Current.CancellationToken);

        var read = await store.GetAsync("tenanta", record.OperationId, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal("tenanta", read.TenantId);
    }

    [Fact]
    public async Task EveryRead_RequiresATenant()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.GetAsync("", "op", TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.ListAsync(new CkMethodOperationQuery { TenantId = " " }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lifecycle_RunningWithProgress_ThenSucceeded_WithResultAndExpiry()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateAsync(record, ct);

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.MarkRunningAsync(TenantA, record.OperationId, Now, ct));
        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.ReportProgressAsync(TenantA, record.OperationId,
            CkMethodOperationProgress.Create(40, "COPYING_COLLECTIONS", ("collection", "assets"), ("done", "4")), Now,
            ct));

        var running = await store.GetAsync(TenantA, record.OperationId, ct);
        Assert.NotNull(running);
        Assert.Equal(CkMethodOperationState.Running, running.State);
        Assert.Equal(40, running.Progress!.Percent);
        Assert.Equal("COPYING_COLLECTIONS", running.Progress.StageCode);
        Assert.Equal("assets", running.Progress.Parameters["collection"]);
        Assert.Equal(Now, running.HeartbeatAtUtc);
        Assert.NotNull(running.StartedAtUtc);

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.CompleteAsync(TenantA, record.OperationId, Success("{\"rows\":42}"), ct));
        var done = await store.GetAsync(TenantA, record.OperationId, ct);

        Assert.NotNull(done);
        Assert.Equal(CkMethodOperationState.Succeeded, done.State);
        Assert.Equal("{\"rows\":42}", done.ResultJson);
        Assert.Equal(Now, done.CompletedAtUtc);
        Assert.Equal(Now + Retention, done.ExpiresAtUtc);
    }

    [Fact]
    public async Task FinalRecord_NeverChangesAgain()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateAsync(record, ct);
        await store.CompleteAsync(TenantA, record.OperationId, Success(), ct);

        Assert.False(await store.MarkRunningAsync(TenantA, record.OperationId, Now, ct));
        Assert.False(await store.HeartbeatAsync(TenantA, record.OperationId, Now, ct));
        Assert.False(await store.ReportProgressAsync(TenantA, record.OperationId,
            CkMethodOperationProgress.Create(99), Now, ct));
        Assert.False(await store.CompleteAsync(TenantA, record.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Failed,
            Error = CkMethodError.Create("EXPORT_FAILED"),
            CompletedAtUtc = Now,
            ExpiresAtUtc = Now + Retention
        }, ct));

        var read = await store.GetAsync(TenantA, record.OperationId, ct);
        Assert.Equal(CkMethodOperationState.Succeeded, read!.State);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task Failed_KeepsErrorCodeAndParameters()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateAsync(record, ct);

        await store.CompleteAsync(TenantA, record.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Failed,
            Error = CkMethodError.Create("EXPORT_FAILED", ("collection", "assets"), ("methodId", MethodOne)),
            CompletedAtUtc = Now,
            ExpiresAtUtc = Now + Retention
        }, ct);
        var read = await store.GetAsync(TenantA, record.OperationId, ct);

        Assert.Equal(CkMethodOperationState.Failed, read!.State);
        Assert.Equal("EXPORT_FAILED", read.Error!.Code);
        Assert.Equal("assets", read.Error.Parameters["collection"]);
        Assert.Null(read.ResultJson);
    }

    [Fact]
    public async Task ExpiredRecord_IsInvisible_BeforeHousekeepingRemovesIt()
    {
        var store = await CreateStoreAsync();
        var record = NewRecord();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateAsync(record, ct);
        await store.CompleteAsync(TenantA, record.OperationId, Success(), ct);

        Time.Advance(Retention - TimeSpan.FromSeconds(1));
        Assert.NotNull(await store.GetAsync(TenantA, record.OperationId, ct));

        Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await store.GetAsync(TenantA, record.OperationId, ct));
        Assert.Empty((await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA }, ct)).Items);
    }

    [Fact]
    public async Task List_FiltersByOwnerMethodStateAndSince_NewestFirst_Paged()
    {
        var store = await CreateStoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var start = Now;
        var user = new CkMethodOperationOwner("user-1", null);
        var other = new CkMethodOperationOwner("user-2", null);
        var client = new CkMethodOperationOwner(null, "svc-client");
        var r1 = NewRecord(owner: user, acceptedAt: start);
        var r2 = NewRecord(owner: user, methodId: MethodTwo, acceptedAt: start.AddMinutes(1));
        var r3 = NewRecord(owner: other, acceptedAt: start.AddMinutes(2));
        var r4 = NewRecord(owner: client, acceptedAt: start.AddMinutes(3));
        var r5 = NewRecord(owner: user, acceptedAt: start.AddMinutes(4));
        var foreign = NewRecord(TenantB, owner: user, acceptedAt: start.AddMinutes(5));
        foreach (var r in new[] { r1, r2, r3, r4, r5, foreign })
        {
            await store.CreateAsync(r, ct);
        }

        await store.CompleteAsync(TenantA, r5.OperationId, Success(), ct);

        async Task<IReadOnlyList<string>> Ids(CkMethodOperationQuery query) =>
            (await store.ListAsync(query, ct)).Items.Select(i => i.OperationId).ToList();

        Assert.Equal([r5.OperationId, r4.OperationId, r3.OperationId, r2.OperationId, r1.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA }));
        Assert.Equal([r5.OperationId, r2.OperationId, r1.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA, Owner = user }));
        Assert.Equal([r4.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA, Owner = new CkMethodOperationOwner(null, "svc-client") }));
        Assert.Equal([r2.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA, Owner = user, MethodId = MethodTwo }));
        Assert.Equal([r5.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA, State = CkMethodOperationState.Succeeded }));
        Assert.Equal([r5.OperationId, r4.OperationId, r3.OperationId],
            await Ids(new CkMethodOperationQuery { TenantId = TenantA, SinceUtc = start.AddMinutes(2) }));

        var page = await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA, Skip = 1, Take = 2 }, ct);
        Assert.Equal([r4.OperationId, r3.OperationId], page.Items.Select(i => i.OperationId));
        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task List_RejectsInvalidPaging()
    {
        var store = await CreateStoreAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListAsync(
            new CkMethodOperationQuery { TenantId = TenantA, Take = CkMethodOperationQuery.MaxTake + 1 },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListAsync(
            new CkMethodOperationQuery { TenantId = TenantA, Skip = -1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailLost_EndsStaleOpenRecordsOnly_AsOutcomeUnknownHandlerLost()
    {
        var store = await CreateStoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var stale = NewRecord();
        var staleRunning = NewRecord(TenantB, MethodTwo);
        var fresh = NewRecord();
        var finished = NewRecord();
        foreach (var r in new[] { stale, staleRunning, fresh, finished })
        {
            await store.CreateAsync(r, ct);
        }

        await store.MarkRunningAsync(TenantB, staleRunning.OperationId, Now, ct);
        await store.CompleteAsync(TenantA, finished.OperationId, Success(), ct);
        Time.Advance(TimeSpan.FromMinutes(5));
        await store.HeartbeatAsync(TenantA, fresh.OperationId, Now, ct);

        var ended = await store.FailLostAsync(Now - TimeSpan.FromMinutes(2), Now, Retention, ct);

        Assert.Equal(2, ended);
        foreach (var (tenant, record) in new[] { (TenantA, stale), (TenantB, staleRunning) })
        {
            var read = await store.GetAsync(tenant, record.OperationId, ct);
            Assert.Equal(CkMethodOperationState.Failed, read!.State);
            Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, read.Error!.Code);
            Assert.Equal(CkMethodOperationReasons.HandlerLost, read.Error.Parameters[CkMethodErrorParameters.Reason]);
            Assert.Equal(record.MethodId, read.Error.Parameters[CkMethodErrorParameters.MethodId]);
            Assert.Equal(Now + Retention, read.ExpiresAtUtc);
            // at most once: an ended operation never starts again
            Assert.False(await store.MarkRunningAsync(tenant, record.OperationId, Now, ct));
        }

        Assert.Equal(CkMethodOperationState.Accepted, (await store.GetAsync(TenantA, fresh.OperationId, ct))!.State);
        Assert.Equal(CkMethodOperationState.Succeeded,
            (await store.GetAsync(TenantA, finished.OperationId, ct))!.State);
        Assert.Equal(0, await store.FailLostAsync(Now - TimeSpan.FromMinutes(2), Now, Retention, ct));
    }

    [Fact]
    public async Task DeleteTenant_RemovesEveryRecordOfThatTenantOnly()
    {
        // AB#8447: a deleted tenant takes its records with it, open and final ones; other tenants keep theirs.
        var store = await CreateStoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var open = NewRecord(TenantA);
        var running = NewRecord(TenantA, MethodTwo);
        var finished = NewRecord(TenantA);
        var other = NewRecord(TenantB);
        foreach (var r in new[] { open, running, finished, other })
        {
            await store.CreateAsync(r, ct);
        }

        await store.MarkRunningAsync(TenantA, running.OperationId, Now, ct);
        await store.CompleteAsync(TenantA, finished.OperationId, Success(), ct);
        var purge = Assert.IsAssignableFrom<ICkMethodOperationTenantPurge>(store);

        var removed = await purge.DeleteTenantAsync("TenantA", ct);

        Assert.Equal(3, removed);
        foreach (var record in new[] { open, running, finished })
        {
            Assert.Null(await store.GetAsync(TenantA, record.OperationId, ct));
        }

        Assert.Equal(0, (await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA }, ct)).TotalCount);
        Assert.NotNull(await store.GetAsync(TenantB, other.OperationId, ct));
        // the work of a removed record completes into nothing; the record is not written again
        Assert.False(await store.CompleteAsync(TenantA, running.OperationId, Success(), ct));
        Assert.Null(await store.GetAsync(TenantA, running.OperationId, ct));
        Assert.Equal(0, await purge.DeleteTenantAsync(TenantA, ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => purge.DeleteTenantAsync(" ", ct));
    }
}
