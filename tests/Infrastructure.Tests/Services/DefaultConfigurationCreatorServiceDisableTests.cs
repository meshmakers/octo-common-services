using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     Pins the AB#4255 pre-disable contract of
///     <see cref="DefaultConfigurationCreatorServiceStandardized.DisableAsync" />: the owning service's
///     <c>GetDisableBlockerAsync</c> answer is consulted after the already-disabled check and before the
///     enabled flag is removed, and a refusal leaves the tenant untouched.
/// </summary>
public class DefaultConfigurationCreatorServiceDisableTests
{
    private const string TenantId = "child-a";
    private const string EnabledKey = "TestServiceEnabled";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly ITenantContext _tenantContext = A.Fake<ITenantContext>();
    private readonly IOctoAdminSession _session = A.Fake<IOctoAdminSession>();

    public DefaultConfigurationCreatorServiceDisableTests()
    {
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(_tenantContext);
        A.CallTo(() => _tenantContext.GetAdminSessionAsync()).Returns(_session);
        GivenFlag(isEnabled: true);
    }

    [Fact]
    public async Task Disable_RemovesTheFlagAndStopsTheTenant_WhenNothingBlocks()
    {
        var sut = new TestCreator(_systemContext, blocker: null);

        await sut.DisableAsync(TenantId);

        A.CallTo(() => _tenantContext.DeleteConfigurationAsync(_session, EnabledKey)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _session.AbortTransactionAsync()).MustNotHaveHappened();
        Assert.Equal([TenantId], sut.StoppedTenants);
        Assert.Equal([TenantId], sut.ConsultedTenants);
    }

    [Fact]
    public async Task Disable_ThrowsConflict_AndLeavesTheFlag_WhenBlocked()
    {
        const string reason = "Communication cannot be disabled for tenant 'child-a' while Pool 'edge-a' (Deployed) is still deployed.";
        var sut = new TestCreator(_systemContext, blocker: reason);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => sut.DisableAsync(TenantId));

        Assert.True(exception.IsConflict);
        Assert.Equal(reason, exception.Message);
        A.CallTo(() => _tenantContext.DeleteConfigurationAsync(A<IOctoAdminSession>._, A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _session.CommitTransactionAsync()).MustNotHaveHappened();
        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        Assert.Empty(sut.StoppedTenants);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Disable_ReportsAlreadyDisabled_BeforeConsultingTheHook(bool? storedFlag)
    {
        GivenFlag(storedFlag);
        var sut = new TestCreator(_systemContext, blocker: "would block if asked");

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => sut.DisableAsync(TenantId));

        Assert.False(exception.IsConflict);
        Assert.Contains("already disabled", exception.Message);
        Assert.Empty(sut.ConsultedTenants);
        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        Assert.Empty(sut.StoppedTenants);
    }

    [Fact]
    public async Task Disable_PropagatesHookFailures_WithoutRemovingTheFlag()
    {
        var sut = new TestCreator(_systemContext, blocker: null,
            hookFailure: new InvalidOperationException("repository unavailable"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.DisableAsync(TenantId));

        Assert.Equal("repository unavailable", exception.Message);
        A.CallTo(() => _tenantContext.DeleteConfigurationAsync(A<IOctoAdminSession>._, A<string>._)).MustNotHaveHappened();
        A.CallTo(() => _session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        Assert.Empty(sut.StoppedTenants);
    }

    [Fact]
    public async Task Disable_DoesNotConsultTheHook_WhenTheServiceCannotBeDisabledAtAll()
    {
        var sut = new TestCreator(_systemContext, blocker: "would block if asked", enabledKey: null);

        await Assert.ThrowsAsync<ConfigurationException>(() => sut.DisableAsync(TenantId));

        Assert.Empty(sut.ConsultedTenants);
        A.CallTo(() => _systemContext.FindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public void ConfigurationException_TenantDisableBlocked_IsAConflictCarryingTheReason()
    {
        var exception = Assert.IsType<ConfigurationException>(ConfigurationException.TenantDisableBlocked("reason"));

        Assert.True(exception.IsConflict);
        Assert.Equal("reason", exception.Message);
        Assert.False(((ConfigurationException)ConfigurationException.TenantIsAutoEnabled("t")).IsConflict);
        Assert.Throws<ArgumentException>(() => ConfigurationException.TenantDisableBlocked(" "));
    }

    private void GivenFlag(bool? isEnabled)
    {
        A.CallTo(() => _tenantContext.GetConfigurationAsync(_session, EnabledKey, A<DefaultConfigurationEnabled?>._))
            .Returns(isEnabled == null ? null : new DefaultConfigurationEnabled { IsEnabled = isEnabled.Value });
    }

    private sealed class TestCreator : DefaultConfigurationCreatorServiceStandardized
    {
        private readonly string? _blocker;
        private readonly Exception? _hookFailure;

        public TestCreator(ISystemContext systemContext, string? blocker, Exception? hookFailure = null,
            string? enabledKey = EnabledKey)
            : base(
                NullLogger<DefaultConfigurationCreatorServiceStandardized>.Instance,
                systemContext,
                A.Fake<ICommandClient<CreateIdentityDataCommandRequest>>(),
                identityDataVersionKey: "test-id-data-version",
                expectedIdentityDataVersion: 1,
                serviceEnabledKey: enabledKey)
        {
            _blocker = blocker;
            _hookFailure = hookFailure;
        }

        public List<string> ConsultedTenants { get; } = [];

        public List<string> StoppedTenants { get; } = [];

        protected override Task SetupTenantAsync(string tenantId) => Task.CompletedTask;

        protected override Task<string?> GetDisableBlockerAsync(string tenantId)
        {
            ConsultedTenants.Add(tenantId);
            if (_hookFailure != null)
            {
                throw _hookFailure;
            }

            return Task.FromResult(_blocker);
        }

        protected override Task StopTenantAsync(string tenantId)
        {
            StoppedTenants.Add(tenantId);
            return Task.CompletedTask;
        }
    }
}
