using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Consumers;

/// <summary>
///     AB#4829. PosUpdateTenant fires on every CK model import, so a tenant delete regularly races a
///     tail of update events still queued per service instance. Consuming such an echo for a tenant
///     whose registry entry is already gone ran SetupAsync into "Tenant does not exist", re-recorded a
///     durable retry row the delete had just cleared, and produced three more bus-retry failures of
///     error-level noise. An update event for an unregistered tenant is by definition such an echo and
///     is dropped. PosCreateTenant must keep its behavior: it is published inside the still-uncommitted
///     create transaction, and the durable setup retry is what covers the record-not-yet-visible race
///     (AB#4690).
/// </summary>
public class PosCreatePosUpdateTenantConsumerTests
{
    private readonly ICkCacheService _ckCacheService = A.Fake<ICkCacheService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IDefaultConfigurationCreatorService _creatorService =
        A.Fake<IDefaultConfigurationCreatorService>();

    private PosCreatePosUpdateTenantConsumer CreateSut() => new(
        NullLogger<PosCreatePosUpdateTenantConsumer>.Instance,
        _ckCacheService, _systemContext, _creatorService);

    private static IDistributedContext<T> ContextFor<T>(T message) where T : class
    {
        var context = A.Fake<IDistributedContext<T>>();
        A.CallTo(() => context.Message).Returns(message);
        return context;
    }

    [Fact]
    public async Task PosUpdate_IsDropped_WhenTheTenantIsNoLongerRegistered()
    {
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(false);

        await CreateSut().ConsumeAsync(ContextFor(new PosUpdateTenant("gone-tenant", Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => _creatorService.SetupAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PosUpdate_RunsSetup_ForARegisteredTenant()
    {
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(true);

        await CreateSut().ConsumeAsync(ContextFor(new PosUpdateTenant("live-tenant", Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => _creatorService.SetupAsync("live-tenant")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PosUpdate_GuardUsesTheRegistryProbe_NotAFullResolve()
    {
        // Review follow-up: TryFindTenantContextAsync builds a tenant context and runs the
        // resolve-time CK model imports — per PosUpdateTenant event, i.e. once per CK import, on top
        // of the resolve SetupAsync does anyway. The guard must use the registry-only probe.
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(true);

        await CreateSut().ConsumeAsync(ContextFor(new PosUpdateTenant("live-tenant", Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => _systemContext.TryFindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PosCreate_RunsSetup_EvenWhenTheTenantIsNotYetVisible()
    {
        // Regression pin: the create event may arrive before the create transaction committed. Gating
        // it on registry visibility would reintroduce the lost-setup hole the durable retry closed.
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(false);

        await CreateSut().ConsumeAsync(ContextFor(new PosCreateTenant("fresh-tenant", Guid.NewGuid(), DateTime.UtcNow)));

        A.CallTo(() => _creatorService.SetupAsync("fresh-tenant")).MustHaveHappenedOnceExactly();
    }
}
