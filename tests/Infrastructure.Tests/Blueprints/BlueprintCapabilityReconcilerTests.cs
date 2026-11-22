using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Blueprints;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Blueprints;

/// <summary>
///     The capability reconciler enables a capability only for the tenant the event names, only when the named
///     blueprint is installed there in exactly that version and only when the reported CK model dependencies need it;
///     it fails closed, never throws, never disables and is idempotent. One test per abuse case.
/// </summary>
public class BlueprintCapabilityReconcilerTests
{
    private const string SystemTenantId = "octosystem";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Starter = "Starter-1.2.0";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantBlueprintInstallations _installations = A.Fake<ITenantBlueprintInstallations>();
    private readonly IConfigurationService _configuration = A.Fake<IConfigurationService>();
    private readonly ITenantContext _tenantContextA = A.Fake<ITenantContext>();
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);

    private readonly BlueprintCapabilityOptions _options = new()
    {
        LookupAttempts = 3, LookupDelay = TimeSpan.Zero, PrerequisiteAttempts = 3, PrerequisiteDelay = TimeSpan.Zero
    };

    public BlueprintCapabilityReconcilerTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns(SystemTenantId);
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync(A<string>._))
            .Returns(Task.FromResult<ITenantRepository?>(null));
        GivenTenant(TenantA);
        GivenTenant(TenantB);
        GivenTenant(SystemTenantId);
        A.CallTo(() => _installations.GetByBlueprintNameAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<BlueprintInstallation?>(null));
        GivenInstalled(TenantA, Starter);

        A.CallTo(() => _configuration.IsEnabledAsync(A<string>._))
            .ReturnsLazily((string t) => Task.FromResult(_enabled.Contains(t)));
        A.CallTo(() => _configuration.EnableAsync(A<string>._))
            .Invokes((string t) => _enabled.Add(t))
            .Returns(Task.CompletedTask);

        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantA)).Returns(_tenantContextA);
        GivenCommunicationFlag(true);
    }

    private void GivenTenant(string tenantId, string? lookupId = null)
    {
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.TenantId).Returns(tenantId);
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync(lookupId ?? tenantId))
            .Returns(Task.FromResult<ITenantRepository?>(repository));
    }

    private void GivenInstalled(string tenantId, string blueprintId)
    {
        var id = new BlueprintId(blueprintId);
        A.CallTo(() => _installations.GetByBlueprintNameAsync(tenantId, id.Name, A<CancellationToken>._))
            .Returns(new BlueprintInstallation
            {
                BlueprintId = id, InstalledAt = DateTime.UtcNow, LastUpdatedAt = DateTime.UtcNow
            });
    }

    private void GivenCommunicationFlag(bool enabled) =>
        A.CallTo(() => _tenantContextA.GetConfigurationAsync(A<IOctoAdminSession>._,
                TenantCapabilityConfigurationKeys.Communication, A<DefaultConfigurationEnabled?>._))
            .Returns(new DefaultConfigurationEnabled { IsEnabled = enabled });

    private BlueprintCapabilityReconciler CreateReconciler() => new(_systemContext, _installations, _configuration,
        Options.Create(_options), NullLogger<BlueprintCapabilityReconciler>.Instance);

    private static BlueprintCapabilityRequest Request(string? tenantId = TenantA, string? blueprintId = Starter,
        params string[]? dependencies) =>
        new(tenantId, blueprintId, dependencies, Guid.NewGuid(), nameof(BlueprintApplied));

    private Task<BlueprintCapabilityOutcome> ReconcileAsync(BlueprintCapabilityRequest request,
        BlueprintCapability capability) =>
        CreateReconciler().ReconcileAsync(request, capability, TestContext.Current.CancellationToken);

    private void AssertNothingEnabled() =>
        A.CallTo(() => _configuration.EnableAsync(A<string>._)).MustNotHaveHappened();

    // ---- acceptance ------------------------------------------------------------------------------

    [Fact]
    public async Task AiBlueprint_EnablesCommunicationAndAi()
    {
        var request = Request(dependencies: ["Basic", "System.Ai"]);

        Assert.Equal(BlueprintCapabilityOutcome.Enabled, await ReconcileAsync(request, BlueprintCapability.Communication));
        _enabled.Clear(); // each service has its own flag; the fake stands for the hosting service
        Assert.Equal(BlueprintCapabilityOutcome.Enabled, await ReconcileAsync(request, BlueprintCapability.Ai));
        A.CallTo(() => _configuration.EnableAsync(TenantA)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task CommunicationOnlyBlueprint_EnablesCommunicationButNotAi()
    {
        var request = Request(dependencies: ["System.Communication"]);

        Assert.Equal(BlueprintCapabilityOutcome.Enabled, await ReconcileAsync(request, BlueprintCapability.Communication));
        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, await ReconcileAsync(request, BlueprintCapability.Ai));
        A.CallTo(() => _configuration.EnableAsync(TenantA)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AlreadyEnabled_IsLeftAlone_Idempotent()
    {
        _enabled.Add(TenantA);
        var request = Request(dependencies: ["System.Communication"]);

        Assert.Equal(BlueprintCapabilityOutcome.AlreadyEnabled,
            await ReconcileAsync(request, BlueprintCapability.Communication));
        Assert.Equal(BlueprintCapabilityOutcome.AlreadyEnabled,
            await ReconcileAsync(request, BlueprintCapability.Communication));
        AssertNothingEnabled();
    }

    [Fact]
    public async Task TenantAndInstallationVisibleOnlyLater_AreWaitedFor()
    {
        // A tenant created with a blueprint publishes the event before the registry commits.
        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.TenantId).Returns(TenantA);
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync(TenantA))
            .ReturnsNextFromSequence(null, repository, repository);
        var installation = new BlueprintInstallation
        {
            BlueprintId = new BlueprintId(Starter), InstalledAt = DateTime.UtcNow, LastUpdatedAt = DateTime.UtcNow
        };
        A.CallTo(() => _installations.GetByBlueprintNameAsync(TenantA, "Starter", A<CancellationToken>._))
            .ReturnsNextFromSequence(null, installation);

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Enabled, outcome);
    }

    // ---- abuse cases -----------------------------------------------------------------------------

    [Fact]
    public async Task A_EventForTenantA_NeverEnablesTenantB()
    {
        await ReconcileAsync(Request(TenantA, dependencies: ["System.Communication"]), BlueprintCapability.Communication);

        A.CallTo(() => _configuration.EnableAsync(TenantA)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _configuration.EnableAsync(A<string>.That.Not.IsEqualTo(TenantA))).MustNotHaveHappened();
    }

    [Fact]
    public async Task A_TenantIdInOtherCase_EnablesTheResolvedTenantOnly()
    {
        GivenTenant(TenantA, "Tenant-A");

        var outcome = await ReconcileAsync(Request("Tenant-A", dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Enabled, outcome);
        A.CallTo(() => _configuration.EnableAsync(TenantA)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _configuration.EnableAsync("Tenant-A")).MustNotHaveHappened();
    }

    [Fact]
    public async Task A_BlueprintInstalledInAnotherTenantOnly_IsRefused()
    {
        // Starter is installed in tenant A; an event that names tenant B must not enable B.
        var outcome = await ReconcileAsync(Request(TenantB, dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
    }

    [Theory]
    [InlineData]
    [InlineData("Basic", "Industry.Basic")]
    public async Task B_BlueprintWithoutTheDependency_EnablesNothing(params string[] dependencies)
    {
        var request = Request(dependencies: dependencies);

        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, await ReconcileAsync(request, BlueprintCapability.Communication));
        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, await ReconcileAsync(request, BlueprintCapability.Ai));
        AssertNothingEnabled();
        // Decided from the event, never from the tenant's CK models (System.Communication is in every tenant).
        A.CallTo(() => _installations.GetByBlueprintNameAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task C_ReinstallOrUpdateThatStillNeedsIt_EnablesAgainAfterAManualDisable()
    {
        var request = Request(dependencies: ["System.Communication"]) with { Trigger = nameof(BlueprintUpdated) };

        // Not enabled (e.g. disabled by hand) and the installed blueprint still needs it: enabled, logged as warning.
        Assert.Equal(BlueprintCapabilityOutcome.Enabled, await ReconcileAsync(request, BlueprintCapability.Communication));
    }

    [Fact]
    public async Task C_UpdateThatNoLongerNeedsIt_NeverEnables()
    {
        GivenInstalled(TenantA, "Starter-2.0.0");

        var outcome = await ReconcileAsync(
            Request(blueprintId: "Starter-2.0.0", dependencies: ["Basic"]) with { Trigger = nameof(BlueprintUpdated) },
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, outcome);
        AssertNothingEnabled();
    }

    [Fact]
    public async Task C_NeverDisables()
    {
        _enabled.Add(TenantA);

        await ReconcileAsync(Request(dependencies: ["Basic"]), BlueprintCapability.Communication);

        A.CallTo(() => _configuration.DisableAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task D_ForgedEvent_BlueprintNotInstalled_IsRefused()
    {
        var outcome = await ReconcileAsync(Request(blueprintId: "Forged-1.0.0", dependencies: ["System.Ai"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
    }

    [Fact]
    public async Task D_ReplayedEvent_OtherVersionInstalled_IsRefusedWithoutWaiting()
    {
        var outcome = await ReconcileAsync(Request(blueprintId: "Starter-1.1.0", dependencies: ["System.Ai"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
        A.CallTo(() => _installations.GetByBlueprintNameAsync(TenantA, "Starter", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task D_UnknownTenant_IsRefusedAfterTheBoundedLookups()
    {
        var outcome = await ReconcileAsync(Request("ghost", dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync("ghost")).MustHaveHappened(3, Times.Exactly);
    }

    [Theory]
    [InlineData(null, Starter)]
    [InlineData(" ", Starter)]
    [InlineData(TenantA, null)]
    [InlineData(TenantA, "not a blueprint id")]
    public async Task D_MissingOrInvalidIds_AreRefused(string? tenantId, string? blueprintId)
    {
        var outcome = await ReconcileAsync(Request(tenantId, blueprintId, "System.Communication"),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
    }

    [Theory]
    [InlineData("System.Communication.Extra")]
    [InlineData("MySystem.Ai")]
    [InlineData("system.communication")]
    [InlineData("System.Ai.Builder")]
    public async Task E_LookAlikeModelNames_DoNotCount(string dependency)
    {
        var request = Request(dependencies: [dependency]);

        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, await ReconcileAsync(request, BlueprintCapability.Communication));
        Assert.Equal(BlueprintCapabilityOutcome.NotRequired, await ReconcileAsync(request, BlueprintCapability.Ai));
        AssertNothingEnabled();
    }

    [Fact]
    public async Task F_SystemTenant_IsNeverEnabled()
    {
        GivenInstalled(SystemTenantId, Starter);

        var outcome = await ReconcileAsync(Request(SystemTenantId, dependencies: ["System.Ai"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
    }

    [Fact]
    public async Task H_AiWithoutCommunication_WaitsBoundedAndGivesUpWithoutEnablingCommunication()
    {
        GivenCommunicationFlag(false);

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Ai"]), BlueprintCapability.Ai);

        Assert.Equal(BlueprintCapabilityOutcome.PrerequisiteMissing, outcome);
        AssertNothingEnabled();
        A.CallTo(() => _tenantContextA.GetConfigurationAsync(A<IOctoAdminSession>._,
                TenantCapabilityConfigurationKeys.Communication, A<DefaultConfigurationEnabled?>._))
            .MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task H_AiWaitsUntilCommunicationIsEnabled()
    {
        A.CallTo(() => _tenantContextA.GetConfigurationAsync(A<IOctoAdminSession>._,
                TenantCapabilityConfigurationKeys.Communication, A<DefaultConfigurationEnabled?>._))
            .ReturnsNextFromSequence(new DefaultConfigurationEnabled { IsEnabled = false },
                new DefaultConfigurationEnabled { IsEnabled = true });

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Ai"]), BlueprintCapability.Ai);

        Assert.Equal(BlueprintCapabilityOutcome.Enabled, outcome);
        A.CallTo(() => _configuration.EnableAsync(TenantA)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task I_EventWithoutDependencyInformation_EnablesNothing()
    {
        var request = new BlueprintCapabilityRequest(TenantA, Starter, null, Guid.NewGuid(), nameof(BlueprintApplied));

        Assert.Equal(BlueprintCapabilityOutcome.NoDependencyInformation,
            await ReconcileAsync(request, BlueprintCapability.Communication));
        Assert.Equal(BlueprintCapabilityOutcome.NoDependencyInformation,
            await ReconcileAsync(request, BlueprintCapability.Ai));
        AssertNothingEnabled();
    }

    [Fact]
    public async Task I_OlderPublisherEvent_MapsToNoDependencyInformation()
    {
        var message = new BlueprintApplied(TenantA, Starter, "Initial", 1, 0, 0, Guid.NewGuid(), DateTime.UtcNow);

        var outcome = await ReconcileAsync(BlueprintCapabilityRequest.From(message), BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.NoDependencyInformation, outcome);
        AssertNothingEnabled();
    }

    // ---- failure handling ------------------------------------------------------------------------

    [Fact]
    public async Task EnableFails_IsReportedAsFailed_AndNeverThrows()
    {
        A.CallTo(() => _configuration.EnableAsync(TenantA)).Throws(new InvalidOperationException("database down"));

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Failed, outcome);
    }

    [Fact]
    public async Task EnableLosesARaceWithAnotherReplica_CountsAsAlreadyEnabled()
    {
        A.CallTo(() => _configuration.EnableAsync(TenantA))
            .Invokes((string t) => _enabled.Add(t))
            .Throws(new InvalidOperationException("already enabled"));

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.AlreadyEnabled, outcome);
    }

    [Fact]
    public async Task TenantLookupThrows_IsRetriedAndThenRefused()
    {
        A.CallTo(() => _systemContext.TryFindTenantRepositoryAsync(TenantA))
            .Throws(new InvalidOperationException("registry unavailable"));

        var outcome = await ReconcileAsync(Request(dependencies: ["System.Communication"]),
            BlueprintCapability.Communication);

        Assert.Equal(BlueprintCapabilityOutcome.Refused, outcome);
        AssertNothingEnabled();
    }

    [Fact]
    public async Task UpdatedEvent_IsMappedWithItsDependencies()
    {
        var message = new BlueprintUpdated(TenantA, Starter, "Starter-1.0.0", "Merge", 0, 1, 0, Guid.NewGuid(),
            DateTime.UtcNow) { CkModelDependencies = ["System.Communication"] };

        var request = BlueprintCapabilityRequest.From(message);

        Assert.Equal(nameof(BlueprintUpdated), request.Trigger);
        Assert.Equal(BlueprintCapabilityOutcome.Enabled, await ReconcileAsync(request, BlueprintCapability.Communication));
    }
}
