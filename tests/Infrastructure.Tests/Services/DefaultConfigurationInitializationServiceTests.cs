using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#4690 — the per-tenant loop of the startup initialization used to be unguarded, so the first
///     tenant whose setup threw aborted it: every remaining tenant was skipped and the host start failed
///     with an AggregateException. A single broken tenant (e.g. one whose database was briefly unreachable
///     right after a delete + recreate) could therefore leave a whole service unprovisioned or
///     crash-looping.
/// </summary>
public class DefaultConfigurationInitializationServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IDefaultConfigurationCreatorService _creator = A.Fake<IDefaultConfigurationCreatorService>();

    public DefaultConfigurationInitializationServiceTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns("octosystem");
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());
    }

    [Fact]
    public async Task OneFailingTenant_DoesNotStopTheOthers_NorFailTheHostStart()
    {
        StubChildTenants("tenant-a", "broken", "tenant-b");
        A.CallTo(() => _creator.SetupAsync("broken")).ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator);

        await sut.InitializeAsync();

        A.CallTo(() => _creator.SetupAsync("tenant-a")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _creator.SetupAsync("broken")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _creator.SetupAsync("tenant-b")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AFailingSystemTenant_StillFailsTheHostStart()
    {
        // Deliberately not guarded: without the system tenant nothing works at all, so this must stay fatal
        // instead of letting a broken instance come up looking healthy.
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem")).ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InitializeAsync());
    }

    private void StubChildTenants(params string[] tenantIds)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items)
            .Returns(tenantIds.Select(t => new OctoTenant(t, t)).ToList());
        A.CallTo(() => _systemContext.GetChildTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }
}
