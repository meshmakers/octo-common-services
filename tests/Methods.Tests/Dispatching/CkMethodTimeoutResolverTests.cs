using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

/// <summary>The timeout of a method comes from the CK cache definition (AB#6906).</summary>
public class CkMethodTimeoutResolverTests
{
    private const string Tenant = "meshtest";
    private const string Model = "Sample.Fleet";

    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly CkMethodDispatcherOptions _options = new();
    private readonly CkCacheMethodTimeoutResolver _resolver;

    public CkMethodTimeoutResolverTests()
    {
        _resolver = new CkCacheMethodTimeoutResolver(Options.Create(_options), _time,
            NullLogger<CkCacheMethodTimeoutResolver>.Instance, _cache);
        A.CallTo(() => _cache.IsTenantLoaded(Tenant)).Returns(true);
        Register("Vehicle", Method("Slow-1", 120), Method("Quick-1", 2), Method("Default-1", null),
            Method("Zero-1", 0), Method("Huge-1", 100000));
    }

    private static CkMethodGraph Method(string methodId, int? timeoutSeconds) =>
        new(new CkId<CkTypeId>($"{Model}-1.0.0/Vehicle"), new CkMethodDto
        {
            MethodId = methodId,
            Execution = timeoutSeconds is { } seconds ? new CkMethodExecutionDto { TimeoutSeconds = seconds } : null
        });

    private void Register(string type, params CkMethodGraph[] methods)
    {
        var graph = new CkTypeGraph(new CkId<CkTypeId>($"{Model}-1.0.0/{type}"), false, false, false, [], null, null, [],
            [], new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [], new CkGraphDirectedAssociations([]), "",
            false, allMethods: methods.ToDictionary(m => m.Definition.MethodId));
        var rtId = new RtCkId<CkTypeId>($"{Model}/{type}");
        A.CallTo(() => _cache.TryGetRtCkType(Tenant, rtId, out graph)).Returns(true);
    }

    [Theory]
    [InlineData("Quick-1", 2)]
    [InlineData("Slow-1", 120)]
    [InlineData("Default-1", 15)] // a method without the key uses 15 s
    [InlineData("Zero-1", 1)] // outside 1-300 cannot occur (compiler) but is clamped
    [InlineData("Huge-1", 300)]
    public void Timeout_IsReadFromTheDefinition_AndClamped(string methodId, int expectedSeconds)
    {
        Assert.True(_resolver.TryGetTimeout(Tenant, $"{Model}/Vehicle", $"{Model}/Vehicle.{methodId}", out var timeout));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timeout);
        Assert.Equal(timeout, _resolver.GetTimeout(Tenant, $"{Model}/Vehicle", methodId)); // plain id works as well
    }

    [Fact]
    public void Deadline_IsNowPlusTheTimeoutOfTheMethod()
    {
        var now = _time.GetUtcNow().UtcDateTime;

        Assert.Equal(now.AddSeconds(2), _resolver.GetDeadlineUtc(Tenant, $"{Model}/Vehicle", "Quick-1"));
    }

    [Theory]
    [InlineData("Unknown-1", "Sample.Fleet/Vehicle", "meshtest")]
    [InlineData("Quick-1", "Sample.Fleet/Unknown", "meshtest")]
    [InlineData("Quick-1", "not a type id", "meshtest")]
    [InlineData("Quick-1", "Sample.Fleet/Vehicle", "unknown-tenant")]
    [InlineData("", "Sample.Fleet/Vehicle", "meshtest")]
    public void UnknownMethod_TypeOrTenant_UsesTheDefault_AndNeverThrows(string methodId, string ckTypeId, string tenant)
    {
        Assert.False(_resolver.TryGetTimeout(tenant, ckTypeId, methodId, out _));
        Assert.Equal(TimeSpan.FromSeconds(15), _resolver.GetTimeout(tenant, ckTypeId, methodId));
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddSeconds(15), _resolver.GetDeadlineUtc(tenant, ckTypeId, methodId));
    }

    [Fact]
    public void CacheFault_UsesTheDefault_AndNeverThrows()
    {
        A.CallTo(() => _cache.IsTenantLoaded(A<string>._)).Throws<InvalidOperationException>();

        Assert.Equal(TimeSpan.FromSeconds(15), _resolver.GetTimeout(Tenant, $"{Model}/Vehicle", "Quick-1"));
    }

    [Fact]
    public void WithoutACkCache_TheDefaultApplies()
    {
        var resolver = new CkCacheMethodTimeoutResolver(Options.Create(_options), _time,
            NullLogger<CkCacheMethodTimeoutResolver>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(15), resolver.GetTimeout(Tenant, $"{Model}/Vehicle", "Quick-1"));
    }

    // ---- the clamping rules behind every deadline -----------------------------------------------------------

    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(10);

    [Fact]
    public void FromTimeout_TimeSpanMaxValue_IsClampedToTheMaximum_NeverOverflows()
    {
        Assert.Equal(Now + Max, CkMethodDeadline.FromTimeout(Now, TimeSpan.MaxValue, Max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromTimeout_NonPositive_IsAlreadyExpired(long ticks) =>
        Assert.Equal(Now, CkMethodDeadline.FromTimeout(Now, TimeSpan.FromTicks(ticks), Max));

    [Fact]
    public void FromTimeout_TimeSpanMinValue_IsAlreadyExpired() =>
        Assert.Equal(Now, CkMethodDeadline.FromTimeout(Now, TimeSpan.MinValue, Max));

    [Fact]
    public void Clamp_KeepsEarlierDeadlines_AndCutsLaterOnes()
    {
        Assert.Equal(Now.AddSeconds(5), CkMethodDeadline.Clamp(Now.AddSeconds(5), Now, Max));
        Assert.Equal(Now + Max, CkMethodDeadline.Clamp(DateTime.MaxValue, Now, Max));
        Assert.Equal(DateTime.MinValue, CkMethodDeadline.Clamp(DateTime.MinValue, Now, Max));
    }
}
