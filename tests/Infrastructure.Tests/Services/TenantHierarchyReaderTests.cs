using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#5060 — the parent-tenant administration rule needs one question answered in the request
///     path: is the addressed tenant a child of the token's tenant? These tests pin the three
///     properties that make that safe to do per request: it is read from the parent's own registry
///     through <see cref="ISystemContext" /> (no new dependency, no tenant-context materialisation),
///     the answer is cached — positive <i>and</i> negative — and an unreadable hierarchy is "not
///     related", never an exception escaping into the request.
/// </summary>
public class TenantHierarchyReaderTests
{
    private const string Parent = "parenttenant";
    private const string Child = "childtenant";
    private const string Stranger = "othertenant";

    private readonly ITenantContext _parentContext = A.Fake<ITenantContext>();
    private readonly IOctoAdminSession _session = A.Fake<IOctoAdminSession>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    public TenantHierarchyReaderTests()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(Parent)).Returns(_parentContext);
        A.CallTo(() => _parentContext.GetAdminSessionAsync()).Returns(_session);
        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Child)).Returns(true);
        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Stranger)).Returns(false);
    }

    private TenantHierarchyReader CreateSut(TimeSpan? cacheDuration = null)
    {
        var options = new TenantAuthorizationOptions();
        if (cacheDuration.HasValue)
        {
            options.TenantHierarchyCacheDuration = cacheDuration.Value;
        }

        return new TenantHierarchyReader(_systemContext, Options.Create(options),
            NullLogger<TenantHierarchyReader>.Instance);
    }

    [Fact]
    public async Task IsChildTenant_ReadsTheParentRegistry()
    {
        var sut = CreateSut();

        Assert.True(await sut.IsChildTenantAsync(Parent, Child));

        // Deliberately the registry probe, not TryGetChildTenantContextAsync — the latter builds a
        // tenant context and runs the CK model auto-imports, which is not a per-request cost.
        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Child)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _parentContext.TryGetChildTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task IsChildTenant_IsFalseForAnUnrelatedTenant()
    {
        var sut = CreateSut();

        Assert.False(await sut.IsChildTenantAsync(Parent, Stranger));
    }

    [Fact]
    public async Task SecondCall_IsAnsweredFromTheCache()
    {
        var sut = CreateSut();

        Assert.True(await sut.IsChildTenantAsync(Parent, Child));
        Assert.True(await sut.IsChildTenantAsync(Parent, Child));

        A.CallTo(() => _systemContext.TryFindTenantContextAsync(Parent)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Child)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NegativeAnswers_AreCachedAsWell()
    {
        // The denial path is the attacker-controllable one: an uncached "no" would turn every 403
        // into a database round trip.
        var sut = CreateSut();

        Assert.False(await sut.IsChildTenantAsync(Parent, Stranger));
        Assert.False(await sut.IsChildTenantAsync(Parent, Stranger));

        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Stranger)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ZeroCacheDuration_ResolvesEveryTime()
    {
        var sut = CreateSut(TimeSpan.Zero);

        Assert.True(await sut.IsChildTenantAsync(Parent, Child));
        Assert.True(await sut.IsChildTenantAsync(Parent, Child));

        A.CallTo(() => _parentContext.IsChildTenantExistingAsync(_session, Child)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task ATenantIsNotItsOwnChild_AndCostsNothing()
    {
        var sut = CreateSut();

        Assert.False(await sut.IsChildTenantAsync(Parent, Parent));

        A.CallTo(() => _systemContext.TryFindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AnUnknownParentTenant_HasNoChildren()
    {
        A.CallTo(() => _systemContext.TryFindTenantContextAsync("ghost")).Returns(Task.FromResult<ITenantContext?>(null));
        var sut = CreateSut();

        Assert.False(await sut.IsChildTenantAsync("ghost", Child));
    }

    [Fact]
    public async Task AnUnreadableHierarchy_FailsClosed()
    {
        // A hierarchy that cannot be read is not a relationship. It must never turn a request the
        // exact tenant match would answer with 403 into a 500.
        A.CallTo(() => _systemContext.TryFindTenantContextAsync(Parent))
            .Throws(new InvalidOperationException("system tenant database not available"));
        var sut = CreateSut();

        Assert.False(await sut.IsChildTenantAsync(Parent, Child));
    }
}
