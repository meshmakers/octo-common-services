using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection;
using FakeItEasy;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>
///     AB#8447: the store registrations also register <see cref="ICkMethodOperationTenantPurge" />, resolving to the store
///     itself; a custom store without a purge gets the no-op.
/// </summary>
public class TenantPurgeRegistrationTests
{
    [Fact]
    public void InMemoryStore_IsAlsoThePurge()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddCkMethodInMemoryOperationStore();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<ICkMethodOperationStore>(),
            provider.GetRequiredService<ICkMethodOperationTenantPurge>());
    }

    [Fact]
    public async Task CustomStoreWithoutPurge_GetsTheNoOp()
    {
        var services = new ServiceCollection();
        services.AddSingleton(A.Fake<ICkMethodOperationStore>());
        services.AddCkMethodOperationTenantPurge();
        await using var provider = services.BuildServiceProvider();

        var purge = provider.GetRequiredService<ICkMethodOperationTenantPurge>();

        Assert.IsType<CkMethodNoOperationTenantPurge>(purge);
        Assert.Equal(0, await purge.DeleteTenantAsync("tenanta", TestContext.Current.CancellationToken));
    }
}
