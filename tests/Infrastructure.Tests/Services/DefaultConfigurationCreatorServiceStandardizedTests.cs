using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     Unit tests for the Phase-2 additions on
///     <see cref="DefaultConfigurationCreatorServiceStandardized" /> — the service-managed
///     blueprint apply loop. The pre-existing Enable/Setup/Start/Stop lifecycle is exercised
///     by integration tests in the host repos (Communication-Controller, Admin-Panel) and
///     is intentionally out of scope here.
/// </summary>
public class DefaultConfigurationCreatorServiceStandardizedTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        A.Fake<ICommandClient<CreateIdentityDataCommandRequest>>();
    private readonly IBlueprintService _blueprintService = A.Fake<IBlueprintService>();

    [Fact]
    public async Task RefreshHook_FiresWhen_DeferTenantStartFalse()
    {
        // Arrange — DeferTenantStart=false simulates the lifecycle-event path
        // (attach / restore / PosCreate / PosUpdate / manual Enable). The base's
        // SetupAsync must invoke the hook after SetupTenantAsync completes.
        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null, embeddedSources: null, prefix: null);
        sut.DeferTenantStart = false;

        // Act
        await sut.PublicSetupAsync("tenant-attach");

        // Assert
        Assert.Equal(1, sut.RefreshCalls);
        Assert.Single(sut.RefreshCallTenants);
        Assert.Equal("tenant-attach", sut.RefreshCallTenants[0]);
    }

    [Fact]
    public async Task RefreshHook_SkippedWhen_DeferTenantStartTrue()
    {
        // Arrange — DeferTenantStart=true simulates the cold-start initialization
        // loop. The refresh hook must NOT fire so a service that uses it for
        // force-re-apply work does not hammer every tenant on every pod restart.
        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null, embeddedSources: null, prefix: null);
        sut.DeferTenantStart = true;

        // Act
        await sut.PublicSetupAsync("tenant-cold");

        // Assert
        Assert.Equal(0, sut.RefreshCalls);
        Assert.Empty(sut.RefreshCallTenants);
    }

    [Fact]
    public async Task NoBlueprintService_IsNoOp()
    {
        // Arrange — service that opted into a prefix but did NOT supply the blueprint engine
        // dependencies. The apply loop must stay silent rather than NRE so subclasses that
        // forget the DI wiring still boot.
        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null,
            embeddedSources: null,
            prefix: "System.Test.");

        // Act + Assert
        await sut.PublicApplyAsync("tenant-a", throwOnFailure: true);
        // No call to anything to verify — the assertion is "did not throw".
    }

    [Fact]
    public async Task NoMatchingBlueprints_DoesNotInvokeService()
    {
        // Arrange — sources contain blueprints from a different service that don't match
        // our prefix. The apply loop must not call ApplyBlueprintAsync at all.
        var sources = new[]
        {
            FakeSource("System.OtherService.A-1.0.0"),
            FakeSource("System.OtherService.B-1.0.0")
        };
        var sut = new TestCreator(_systemContext, _commandClient,
            _blueprintService, sources, prefix: "System.Test.");

        await sut.PublicApplyAsync("tenant-a", throwOnFailure: false);

        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                A<string>._, A<BlueprintId>._, A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task MultipleVersionsOfSameName_PicksLatest()
    {
        // Arrange — same blueprint name registered at versions 1.0.0 and 1.2.0; latest wins.
        var sources = new[]
        {
            FakeSource("System.Test.Foo-1.0.0"),
            FakeSource("System.Test.Foo-1.2.0"),
            FakeSource("System.Test.Foo-1.1.0")
        };
        var sut = new TestCreator(_systemContext, _commandClient,
            _blueprintService, sources, prefix: "System.Test.");

        StubApply(success: true);

        await sut.PublicApplyAsync("tenant-a", throwOnFailure: true);

        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                "tenant-a",
                A<BlueprintId>.That.Matches(b => b.FullName == "System.Test.Foo-1.2.0"),
                A<bool>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task OverrideIsServiceManagedBlueprint_AppliesExceptionBlueprint()
    {
        // Arrange — emulates Admin-Panel's pattern: prefix "System.UI." plus a hand-picked
        // additional blueprint outside the prefix ("System.TenantMode").
        var sources = new[]
        {
            FakeSource("System.UI.Cockpit-1.0.0"),
            FakeSource("System.TenantMode-1.0.0"),
            FakeSource("System.Communication.Release-1.0.0") // out — owned by another service
        };
        var sut = new ExtendedTestCreator(_systemContext, _commandClient,
            _blueprintService, sources);

        StubApply(success: true);

        await sut.PublicApplyAsync("tenant-a", throwOnFailure: true);

        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                "tenant-a",
                A<BlueprintId>.That.Matches(b => b.FullName == "System.UI.Cockpit-1.0.0"),
                A<bool>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                "tenant-a",
                A<BlueprintId>.That.Matches(b => b.FullName == "System.TenantMode-1.0.0"),
                A<bool>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                "tenant-a",
                A<BlueprintId>.That.Matches(b => b.FullName.StartsWith("System.Communication.")),
                A<bool>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task SkippedResult_IsTreatedAsSuccess()
    {
        // Arrange — single blueprint that returns Skipped (e.g. requires: didn't match).
        // Apply loop must not throw, must not call the failure hook.
        var sources = new[] { FakeSource("System.Test.Foo-1.0.0") };
        var sut = new TestCreator(_systemContext, _commandClient,
            _blueprintService, sources, prefix: "System.Test.");

        var blueprintId = new BlueprintId("System.Test.Foo-1.0.0");
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                A<string>._, A<BlueprintId>._, A<bool>._, A<CancellationToken>._))
            .Returns(BlueprintApplicationResult.Skipped(
                "tenant-a", blueprintId, "test requires mismatch", new OperationResult()));

        await sut.PublicApplyAsync("tenant-a", throwOnFailure: true);

        Assert.Empty(sut.FailedHookCalls);
    }

    [Fact]
    public async Task ThrowOnFailureTrue_ThrowsOnFirstFailure()
    {
        var sources = new[]
        {
            FakeSource("System.Test.Foo-1.0.0"),
            FakeSource("System.Test.Bar-1.0.0")
        };
        var sut = new TestCreator(_systemContext, _commandClient,
            _blueprintService, sources, prefix: "System.Test.");

        StubApply(success: false);

        await Assert.ThrowsAsync<InitializationException>(() =>
            sut.PublicApplyAsync("tenant-a", throwOnFailure: true));
    }

    [Fact]
    public async Task ThrowOnFailureFalse_CallsHookAndContinues()
    {
        var sources = new[]
        {
            FakeSource("System.Test.Foo-1.0.0"),
            FakeSource("System.Test.Bar-1.0.0")
        };
        var sut = new TestCreator(_systemContext, _commandClient,
            _blueprintService, sources, prefix: "System.Test.");

        StubApply(success: false);

        await sut.PublicApplyAsync("tenant-a", throwOnFailure: false);

        // Both blueprints should be tried; the failure hook should fire for each.
        Assert.Equal(2, sut.FailedHookCalls.Count);
        Assert.Contains(sut.FailedHookCalls,
            c => c.blueprintId.FullName == "System.Test.Foo-1.0.0");
        Assert.Contains(sut.FailedHookCalls,
            c => c.blueprintId.FullName == "System.Test.Bar-1.0.0");
    }

    // ---------- AB#4208: FailedTenantHasNoIdentityCk must be retriable ----------

    [Fact]
    public async Task CheckSetupIdentity_ChildTenant_FailedTenantHasNoIdentityCk_Throws()
    {
        // Arrange — the Identity service has not yet imported the Identity CK into the
        // child tenant when the consumer is asked to create identity data, so it answers
        // with FailedTenantHasNoIdentityCk. The standardized creator must throw so the
        // outer SetupTenantAsync / RetryFailedTenantsAsync path buffers the tenant for
        // background retry instead of silently dropping it (see AB#4208).
        StubCreateIdentityDataResponse(CreateIdentityDataResult.FailedTenantHasNoIdentityCk);
        A.CallTo(() => _systemContext.TenantId).Returns("octosystem");

        var session = A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.IOctoAdminSession>();
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.TenantId).Returns("child-tenant");

        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null, embeddedSources: null, prefix: null);

        // Act + Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PublicCheckSetupIdentityDataAsync(session, tenantContext));
    }

    [Fact]
    public async Task CheckSetupIdentity_ChildTenant_Success_DoesNotThrow()
    {
        // Positive control — the Success path must remain a normal return so a healthy
        // tenant is not put on the retry list.
        StubCreateIdentityDataResponse(CreateIdentityDataResult.Success);
        A.CallTo(() => _systemContext.TenantId).Returns("octosystem");

        var session = A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.IOctoAdminSession>();
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.TenantId).Returns("child-tenant");

        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null, embeddedSources: null, prefix: null);

        // Act — must complete without throwing
        await sut.PublicCheckSetupIdentityDataAsync(session, tenantContext);
    }

    [Fact]
    public async Task CheckSetupIdentity_SystemTenant_FailedTenantHasNoIdentityCk_Throws()
    {
        // Same race can happen for the system tenant itself on a cold-start where the
        // Identity service has not yet finished its own bootstrap. Throwing keeps the
        // behaviour symmetrical so the system tenant also lands on the retry list.
        StubCreateIdentityDataResponse(CreateIdentityDataResult.FailedTenantHasNoIdentityCk);
        A.CallTo(() => _systemContext.TenantId).Returns("octosystem");

        // Configure system-tenant version lookup to return a "not yet provisioned" value so
        // CheckSetupIdentityDataAsync does NOT early-return before sending the command.
        A.CallTo(() => _systemContext.GetConfigurationAsync(
                A<Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.IOctoAdminSession>._,
                A<string>._,
                A<DefaultConfigurationVersion>._))
            .Returns(Task.FromResult<DefaultConfigurationVersion?>(
                new DefaultConfigurationVersion { Version = -1 }));

        var session = A.Fake<Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.IOctoAdminSession>();
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.TenantId).Returns("octosystem");

        var sut = new TestCreator(_systemContext, _commandClient,
            blueprintService: null, embeddedSources: null, prefix: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.PublicCheckSetupIdentityDataAsync(session, tenantContext));
    }

    private void StubCreateIdentityDataResponse(CreateIdentityDataResult result)
    {
        A.CallTo(() => _commandClient.GetResponseWithRetry<EnumCommandResponse<CreateIdentityDataResult>>(
                A<CreateIdentityDataCommandRequest>._,
                A<int>._,
                A<TimeSpan?>._,
                A<CancellationToken>._,
                A<TimeSpan?>._))
            .Returns(Task.FromResult(new EnumCommandResponse<CreateIdentityDataResult> { Response = result }));
    }

    // ---------- helpers ----------

    private void StubApply(bool success)
    {
        A.CallTo(() => _blueprintService.ApplyBlueprintAsync(
                A<string>._, A<BlueprintId>._, A<bool>._, A<CancellationToken>._))
            .ReturnsLazily((string tid, BlueprintId bid, bool _, CancellationToken _) =>
                success
                    ? BlueprintApplicationResult.Success(
                        tid, bid,
                        new List<CkModelIdVersionRange>(),
                        new List<string>(),
                        entitiesCreated: 0,
                        new OperationResult())
                    : BlueprintApplicationResult.Failed(new OperationResult()));
    }

    private static IBlueprintEmbeddedSource FakeSource(string fullName)
    {
        var src = A.Fake<IBlueprintEmbeddedSource>();
        A.CallTo(() => src.BlueprintId).Returns(new BlueprintId(fullName));
        return src;
    }

    /// <summary>
    ///     Minimal subclass that exposes the protected apply method publicly and records
    ///     failure-hook invocations for test assertions. Prefix is supplied per-instance.
    /// </summary>
    private sealed class TestCreator : DefaultConfigurationCreatorServiceStandardized
    {
        private readonly string? _prefix;

        public TestCreator(
            ISystemContext systemContext,
            ICommandClient<CreateIdentityDataCommandRequest> commandClient,
            IBlueprintService? blueprintService,
            IEnumerable<IBlueprintEmbeddedSource>? embeddedSources,
            string? prefix)
            : base(
                NullLogger<DefaultConfigurationCreatorServiceStandardized>.Instance,
                systemContext,
                commandClient,
                identityDataVersionKey: "test-id-data-version",
                expectedIdentityDataVersion: 1,
                blueprintService: blueprintService,
                embeddedBlueprintSources: embeddedSources)
        {
            _prefix = prefix;
        }

        public List<(string TenantId, BlueprintId blueprintId, OperationResult result)> FailedHookCalls { get; } = new();

        public int RefreshCalls { get; private set; }
        public List<string> RefreshCallTenants { get; } = new();

        protected override string? ServiceManagedBlueprintPrefix => _prefix;

        public Task PublicApplyAsync(string tenantId, bool throwOnFailure) =>
            ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure);

        public Task PublicSetupAsync(string tenantId) => SetupAsync(tenantId);

        public Task PublicCheckSetupIdentityDataAsync(
            Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.IOctoAdminSession session,
            ITenantContext tenantContext) =>
            CheckSetupIdentityDataAsync(session, tenantContext);

        protected override Task SetupTenantAsync(string tenantId) => Task.CompletedTask;

        protected override Task RefreshTenantStateAsync(string tenantId)
        {
            RefreshCalls++;
            RefreshCallTenants.Add(tenantId);
            return Task.CompletedTask;
        }

        protected override Task OnServiceManagedBlueprintApplyFailedAsync(
            string tenantId, BlueprintId blueprintId, OperationResult operationResult, CancellationToken cancellationToken)
        {
            FailedHookCalls.Add((tenantId, blueprintId, operationResult));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Variant that overrides <c>IsServiceManagedBlueprint</c> to match the Admin-Panel
    ///     pattern: prefix <c>System.UI.</c> plus an explicit allowlist for <c>System.TenantMode</c>.
    /// </summary>
    private sealed class ExtendedTestCreator : DefaultConfigurationCreatorServiceStandardized
    {
        public ExtendedTestCreator(
            ISystemContext systemContext,
            ICommandClient<CreateIdentityDataCommandRequest> commandClient,
            IBlueprintService blueprintService,
            IEnumerable<IBlueprintEmbeddedSource> embeddedSources)
            : base(
                NullLogger<DefaultConfigurationCreatorServiceStandardized>.Instance,
                systemContext,
                commandClient,
                identityDataVersionKey: "test-id-data-version",
                expectedIdentityDataVersion: 1,
                blueprintService: blueprintService,
                embeddedBlueprintSources: embeddedSources)
        {
        }

        protected override string ServiceManagedBlueprintPrefix => "System.UI.";

        protected override bool IsServiceManagedBlueprint(BlueprintId blueprintId) =>
            base.IsServiceManagedBlueprint(blueprintId) || blueprintId.Name == "System.TenantMode";

        public Task PublicApplyAsync(string tenantId, bool throwOnFailure) =>
            ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure);
    }
}
