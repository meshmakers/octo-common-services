using Meshmakers.Octo.Services.Methods.Operations;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>AB#8443: the store contract against <see cref="CkMethodInMemoryOperationStore" />.</summary>
public class InMemoryOperationStoreTests : OperationStoreContract
{
    protected override Task<ICkMethodOperationStore> CreateStoreAsync() =>
        Task.FromResult<ICkMethodOperationStore>(new CkMethodInMemoryOperationStore(Time));

    [Fact]
    public async Task Sweep_RemovesExpiredRecords_SoTheStoreDoesNotGrowWithoutBound()
    {
        var store = new CkMethodInMemoryOperationStore(Time);
        var ct = TestContext.Current.CancellationToken;
        var expired = NewRecord();
        var kept = NewRecord();
        await store.CreateAsync(expired, ct);
        await store.CreateAsync(kept, ct);
        await store.CompleteAsync(TenantA, expired.OperationId, new CkMethodOperationCompletion
        {
            State = CkMethodOperationState.Succeeded, CompletedAtUtc = Now, ExpiresAtUtc = Now + TimeSpan.FromHours(1)
        }, ct);
        await store.HeartbeatAsync(TenantA, kept.OperationId, Now + TimeSpan.FromHours(2), ct);
        Time.Advance(TimeSpan.FromHours(2));

        await store.FailLostAsync(Now - TimeSpan.FromMinutes(2), Now, Retention, ct);

        Assert.Equal(1, store.Count);
        Assert.NotNull(await store.GetAsync(TenantA, kept.OperationId, ct));
    }
}
