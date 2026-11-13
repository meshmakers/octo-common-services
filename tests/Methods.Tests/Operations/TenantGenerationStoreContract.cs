using FakeItEasy;
using Meshmakers.Octo.Services.Methods.Operations;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>A generation source the test sets per tenant (<c>null</c> = unknown).</summary>
internal sealed class SettableTenantGeneration : ICkMethodOperationTenantGeneration
{
    private readonly Dictionary<string, DateTime?> _starts = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Failure { get; set; }

    public void Set(string tenantId, DateTime? startUtc) => _starts[tenantId] = startUtc;

    public Task<DateTime?> GetStartUtcAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(_starts.TryGetValue(tenantId, out var start) ? start : null);
    }
}

/// <summary>
///     AB#8881 (security): a tenant created again with the id of a deleted tenant never reads the deleted tenant's operation
///     records — neither those a failed purge left behind nor one a call admitted before the delete wrote after the purge.
///     Runs against every store (linked into <c>Methods.IntegrationTests</c> for MongoDB), and the whole store contract
///     holds through the guard.
/// </summary>
public abstract class TenantGenerationStoreContract : OperationStoreContract
{
    private readonly SettableTenantGeneration _generation = new();

    /// <summary>A fresh, empty store to guard (in-memory here, MongoDB in <c>Methods.IntegrationTests</c>).</summary>
    protected abstract ICkMethodOperationStore CreateInner();

    protected override Task<ICkMethodOperationStore> CreateStoreAsync() =>
        Task.FromResult<ICkMethodOperationStore>(Guarded());

    private CkMethodTenantGenerationOperationStore Guarded(ICkMethodOperationStore? inner = null) =>
        new(inner ?? CreateInner(), _generation);

    [Fact]
    public async Task FailedPurgeOnDelete_ThenRecreateSameId_GetAndListReturnNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Guarded();
        _generation.Set(TenantA, Now - TimeSpan.FromDays(1));
        var finished = NewRecord(TenantA);
        var open = NewRecord(TenantA, MethodTwo);
        await store.CreateAsync(finished, ct);
        await store.CreateAsync(open, ct);
        await store.CompleteAsync(TenantA, finished.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded, ResultJson = "\"secret-ish\"", CompletedAtUtc = Now,
            ExpiresAtUtc = Now + Retention
        }, ct);

        // the tenant is deleted, the purge fails (records stay), the id is created again two minutes later
        Time.Advance(TimeSpan.FromMinutes(2));
        _generation.Set(TenantA, Now);

        Assert.Null(await store.GetAsync(TenantA, finished.OperationId, ct));
        Assert.Null(await store.GetAsync("TENANTA", open.OperationId, ct));
        var page = await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA }, ct);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        // an explicit, earlier "since" does not reach back into the earlier generation
        var since = await store.ListAsync(
            new CkMethodOperationQuery { TenantId = TenantA, SinceUtc = Now - TimeSpan.FromDays(30) }, ct);
        Assert.Equal(0, since.TotalCount);
        // the records are still there (the purge failed) — only invisible
        Assert.NotNull(await store.Inner.GetAsync(TenantA, finished.OperationId, ct));
        Assert.NotNull(await store.Inner.GetAsync(TenantA, open.OperationId, ct));
    }

    [Fact]
    public async Task RecordWrittenAfterPurge_BeforeRecreate_IsInvisibleToTheNewTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Guarded();
        _generation.Set(TenantA, Now - TimeSpan.FromDays(1));
        var admittedAt = Now;

        // delete: the purge runs and succeeds
        Time.Advance(TimeSpan.FromSeconds(1));
        await store.DeleteTenantAsync(TenantA, ct);

        // a call admitted just before the delete writes its record now
        Time.Advance(TimeSpan.FromSeconds(1));
        var late = NewRecord(TenantA, acceptedAt: admittedAt);
        await store.CreateAsync(late, ct);

        // the id is created again after the delete settled; the new tenant starts its own operation
        Time.Advance(TimeSpan.FromSeconds(90));
        _generation.Set(TenantA, Now);
        Time.Advance(TimeSpan.FromSeconds(5));
        var own = NewRecord(TenantA);
        await store.CreateAsync(own, ct);

        Assert.Null(await store.GetAsync(TenantA, late.OperationId, ct));
        var page = await store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA }, ct);
        Assert.Equal(new[] { own.OperationId }, page.Items.Select(r => r.OperationId));
        Assert.Equal(1, page.TotalCount);
        Assert.NotNull(await store.GetAsync(TenantA, own.OperationId, ct));
    }

    [Fact]
    public async Task List_KeepsALaterSince_AndOtherTenantsAreUnaffected()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Guarded();
        var start = Now - TimeSpan.FromHours(1);
        _generation.Set(TenantA, start);
        var early = NewRecord(TenantA, acceptedAt: Now - TimeSpan.FromMinutes(30));
        var recent = NewRecord(TenantA, acceptedAt: Now - TimeSpan.FromMinutes(5));
        var otherOld = NewRecord(TenantB, acceptedAt: Now - TimeSpan.FromDays(2));
        foreach (var r in new[] { early, recent, otherOld })
        {
            await store.CreateAsync(r, ct);
        }

        var later = await store.ListAsync(
            new CkMethodOperationQuery { TenantId = TenantA, SinceUtc = Now - TimeSpan.FromMinutes(10) }, ct);
        Assert.Equal(new[] { recent.OperationId }, later.Items.Select(r => r.OperationId));
        // tenant B has no known generation: nothing is filtered
        Assert.NotNull(await store.GetAsync(TenantB, otherOld.OperationId, ct));
    }

    [Fact]
    public async Task UnreadableGeneration_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Guarded();
        var record = NewRecord(TenantA);
        await store.CreateAsync(record, ct);
        _generation.Failure = new CkMethodOperationStoreException("lifecycle store down");

        await Assert.ThrowsAsync<CkMethodOperationStoreException>(() =>
            store.GetAsync(TenantA, record.OperationId, ct));
        await Assert.ThrowsAsync<CkMethodOperationStoreException>(() =>
            store.ListAsync(new CkMethodOperationQuery { TenantId = TenantA }, ct));
    }

    [Fact]
    public async Task HandlerWrites_StillReachTheRecord()
    {
        // the guard filters reads only: the handler that accepted an operation keeps updating its own record
        var ct = TestContext.Current.CancellationToken;
        var inner = CreateInner();
        var store = Guarded(inner);
        var record = NewRecord(TenantA);
        await store.CreateAsync(record, ct);
        _generation.Set(TenantA, Now + TimeSpan.FromMinutes(1));

        Assert.True(await store.MarkRunningAsync(TenantA, record.OperationId, Now, ct));
        Assert.True(await store.HeartbeatAsync(TenantA, record.OperationId, Now, ct));
        Assert.Equal(CkMethodOperationState.Running, (await inner.GetAsync(TenantA, record.OperationId, ct))!.State);
    }

    [Fact]
    public async Task Purge_OfAStoreWithoutPurge_RemovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Guarded(A.Fake<ICkMethodOperationStore>());

        Assert.Equal(0, await store.DeleteTenantAsync(TenantA, ct));
        Assert.Equal(0, await store.DeleteTenantAcceptedBeforeAsync(TenantA, Now, ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteTenantAsync(" ", ct));
    }
}
