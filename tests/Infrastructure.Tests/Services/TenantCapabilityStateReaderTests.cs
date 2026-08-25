using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#4255 — the tenant delete/detach guard needs one answer to "which capabilities are still
///     enabled for this tenant", read from the tenant's own configuration store. The reader must treat
///     a missing key (Communication, Reporting and AI delete it on Disable) and a kept
///     <c>IsEnabled = false</c> flag (Stream Data) the same way, and must never turn a read failure
///     into "disabled".
/// </summary>
public class TenantCapabilityStateReaderTests
{
    private readonly ITenantContext _parent = A.Fake<ITenantContext>();
    private readonly ITenantContext _child = A.Fake<ITenantContext>();
    private readonly IOctoAdminSession _parentSession = A.Fake<IOctoAdminSession>();
    private readonly IOctoAdminSession _childSession = A.Fake<IOctoAdminSession>();
    private readonly TenantCapabilityStateReader _sut = new(NullLogger<TenantCapabilityStateReader>.Instance);

    public TenantCapabilityStateReaderTests()
    {
        A.CallTo(() => _parent.GetAdminSessionAsync()).Returns(_parentSession);
        A.CallTo(() => _child.GetAdminSessionAsync()).Returns(_childSession);
        A.CallTo(() => _child.TenantId).Returns("child-a");
        A.CallTo(() => _parent.TryGetChildTenantContextAsync(_parentSession, "child-a")).Returns(_child);
    }

    private void SetFlag(string key, bool? isEnabled)
    {
        A.CallTo(() => _child.GetConfigurationAsync(_childSession, key, A<DefaultConfigurationEnabled?>._))
            .Returns(isEnabled == null ? null : new DefaultConfigurationEnabled { IsEnabled = isEnabled.Value });
    }

    private void SetAllFlags(bool? isEnabled)
    {
        SetFlag(TenantCapabilityConfigurationKeys.Communication, isEnabled);
        SetFlag(TenantCapabilityConfigurationKeys.Reporting, isEnabled);
        SetFlag(TenantCapabilityConfigurationKeys.AiServices, isEnabled);
    }

    [Fact]
    public async Task GetEnabled_ReturnsEmpty_WhenNoFlagExists()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        SetAllFlags(null);

        var result = await _sut.GetEnabledCapabilitiesAsync(_child);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEnabled_TreatsAKeptFalseFlagAsDisabled()
    {
        // Stream Data keeps its key with IsEnabled = false after a Disable; a reader that took the
        // key's presence as "enabled" would make such a tenant undeletable.
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        SetAllFlags(false);

        var result = await _sut.GetEnabledCapabilitiesAsync(_child);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEnabled_ReportsEnabledCapabilities_InDeclarationOrder()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(true);
        SetAllFlags(true);

        var result = await _sut.GetEnabledCapabilitiesAsync(_child);

        Assert.Equal(
            [
                TenantCapability.StreamData, TenantCapability.Communication, TenantCapability.Reporting,
                TenantCapability.AiServices,
            ],
            result);
    }

    [Fact]
    public async Task GetEnabled_ReportsOnlyTheEnabledOnes()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        SetFlag(TenantCapabilityConfigurationKeys.Communication, true);
        SetFlag(TenantCapabilityConfigurationKeys.Reporting, null);
        SetFlag(TenantCapabilityConfigurationKeys.AiServices, true);

        var result = await _sut.GetEnabledCapabilitiesAsync(_child);

        Assert.Equal([TenantCapability.Communication, TenantCapability.AiServices], result);
    }

    [Fact]
    public async Task GetEnabled_ReadsTheKeysTheOwningServicesWrite()
    {
        // The literals are what the Communication Controller, Reporting and AI creators pass to the
        // Standardized base as serviceEnabledKey; a drift here silently disables the guard.
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        SetAllFlags(null);

        await _sut.GetEnabledCapabilitiesAsync(_child);

        A.CallTo(() => _child.IsStreamDataEnabledAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _child.GetConfigurationAsync(_childSession, "CommunicationControllerServicesEnabled",
            A<DefaultConfigurationEnabled?>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _child.GetConfigurationAsync(_childSession, "ReportingServices",
            A<DefaultConfigurationEnabled?>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _child.GetConfigurationAsync(_childSession, "AiServicesEnabled",
            A<DefaultConfigurationEnabled?>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetEnabled_CommitsTheChildSession()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        SetAllFlags(null);

        await _sut.GetEnabledCapabilitiesAsync(_child);

        A.CallTo(() => _childSession.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _childSession.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _childSession.AbortTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetEnabled_PropagatesReadFailures_AndAbortsTheSession()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(false);
        A.CallTo(() => _child.GetConfigurationAsync(_childSession, A<string>._, A<DefaultConfigurationEnabled?>._))
            .Throws(new InvalidOperationException("mongo down"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.GetEnabledCapabilitiesAsync(_child));

        Assert.Equal("mongo down", ex.Message);
        A.CallTo(() => _childSession.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _childSession.CommitTransactionAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetEnabled_ResolvesTheChildThroughTheParent()
    {
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).Returns(true);
        SetAllFlags(null);

        var result = await _sut.GetEnabledCapabilitiesAsync(_parent, "child-a");

        Assert.Equal([TenantCapability.StreamData], result);
        A.CallTo(() => _parentSession.StartTransaction()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _parentSession.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetEnabled_ThrowsTenantNotFound_WhenTheChildIsNotResolvable()
    {
        A.CallTo(() => _parent.TryGetChildTenantContextAsync(_parentSession, "stranger"))
            .Returns((ITenantContext?)null);

        var ex = await Assert.ThrowsAsync<TenantException>(() =>
            _sut.GetEnabledCapabilitiesAsync(_parent, "stranger"));

        Assert.True(ex.IsTenantNotFound);
        A.CallTo(() => _child.IsStreamDataEnabledAsync()).MustNotHaveHappened();
    }
}
