using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>AB#8881: the generation guard over the in-memory store.</summary>
public class InMemoryTenantGenerationStoreTests : TenantGenerationStoreContract
{
    protected override ICkMethodOperationStore CreateInner() => new CkMethodInMemoryOperationStore(Time);
}

/// <summary>AB#8881: the generation from the tenant lifecycle record.</summary>
public class TenantLifecycleGenerationTests
{
    private readonly ITenantLifecycleStore _lifecycle = A.Fake<ITenantLifecycleStore>();

    [Fact]
    public async Task ReturnsTheCreationTimeOfTheNormalizedTenant_AsUtc()
    {
        var created = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Unspecified);
        A.CallTo(() => _lifecycle.GetAsync("tenanta", A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = "tenanta", CreatedUtc = created });

        var start = await new CkMethodTenantLifecycleGeneration(_lifecycle)
            .GetStartUtcAsync(" TenantA ", TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeKind.Utc, start!.Value.Kind);
        Assert.Equal(created.Ticks, start.Value.Ticks);
    }

    [Fact]
    public async Task NoRecordOrNoCreationTime_IsUnknown()
    {
        var generation = new CkMethodTenantLifecycleGeneration(_lifecycle);
        A.CallTo(() => _lifecycle.GetAsync("legacy", A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = "legacy" });
        A.CallTo(() => _lifecycle.GetAsync("missing", A<CancellationToken>._))
            .Returns(Task.FromResult<TenantLifecycleRecord?>(null));

        Assert.Null(await generation.GetStartUtcAsync("legacy", TestContext.Current.CancellationToken));
        Assert.Null(await generation.GetStartUtcAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnreadableLifecycleStore_IsAStoreFailure()
    {
        A.CallTo(() => _lifecycle.GetAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("db"));

        await Assert.ThrowsAsync<CkMethodOperationStoreException>(() =>
            new CkMethodTenantLifecycleGeneration(_lifecycle)
                .GetStartUtcAsync("tenanta", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registration_UsesTheLifecycleStoreWhenTheHostHasIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_lifecycle);
        services.AddCkMethodInMemoryOperationStore();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<CkMethodTenantLifecycleGeneration>(
            provider.GetRequiredService<ICkMethodOperationTenantGeneration>());
        var store = Assert.IsType<CkMethodTenantGenerationOperationStore>(
            provider.GetRequiredService<ICkMethodOperationStore>());
        Assert.IsType<CkMethodInMemoryOperationStore>(store.Inner);
    }

    [Fact]
    public async Task Registration_WithoutLifecycleStore_FiltersNothing()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddCkMethodInMemoryOperationStore();
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<CkMethodNoOperationTenantGeneration>(
            provider.GetRequiredService<ICkMethodOperationTenantGeneration>());
    }
}
