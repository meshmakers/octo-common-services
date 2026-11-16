using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.TenantGeneration;

/// <summary>
///     AB#8999: the tenant create, update and delete events drop the cached generation where the host receives them, and
///     the registration resolves without a lifecycle store.
/// </summary>
public class TenantGenerationInvalidationTests
{
    private const string Tenant = "meshtest";

    private static IDistributedContext<T> ContextFor<T>(T message) where T : class
    {
        var context = A.Fake<IDistributedContext<T>>();
        A.CallTo(() => context.Message).Returns(message);
        return context;
    }

    [Fact]
    public async Task PreDeleteAndPreUpdate_InvalidateTheGeneration()
    {
        var reader = A.Fake<ITenantGenerationReader>();
        var consumer = new PreUpdatePreDeleteTenantConsumer(NullLogger<PreUpdatePreDeleteTenantConsumer>.Instance,
            A.Fake<ICkCacheService>(), A.Fake<ISystemContext>(), reader);

        await consumer.ConsumeAsync(ContextFor(new PreDeleteTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));
        await consumer.ConsumeAsync(ContextFor(new PreUpdateTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => reader.Invalidate(Tenant)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task PosCreateAndPosUpdate_InvalidateTheGeneration()
    {
        var reader = A.Fake<ITenantGenerationReader>();
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(true);
        var consumer = new PosCreatePosUpdateTenantConsumer(NullLogger<PosCreatePosUpdateTenantConsumer>.Instance,
            A.Fake<ICkCacheService>(), systemContext, A.Fake<IDefaultConfigurationCreatorService>(), reader);

        await consumer.ConsumeAsync(ContextFor(new PosCreateTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));
        await consumer.ConsumeAsync(ContextFor(new PosUpdateTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => reader.Invalidate(Tenant)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public void Registration_ResolvesTheSharedSingletons_WithoutALifecycleStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOctoTenantGenerationCheck();
        services.AddOctoTenantGenerationCheck(); // idempotent

        using var provider = services.BuildServiceProvider();
        var check = provider.GetRequiredService<ITenantGenerationCheck>();

        Assert.Same(check, provider.GetRequiredService<ITenantGenerationCheck>());
        Assert.Single(services, d => d.ServiceType == typeof(ITenantGenerationCheck));
        Assert.NotNull(provider.GetRequiredService<ITenantGenerationReader>());
    }
}
