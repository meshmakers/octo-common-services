using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.Logging.Abstractions;
using FakeItEasy;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

/// <summary>
///     The type graphs below are shaped exactly like the engine's <c>InheritanceResolver</c> produces them (see
///     <c>CkV2MethodResolverTests.Methods_AreInheritedWithTheirDeclaringType</c> and the interface completion tests in
///     octo-construction-kit-engine): <c>AllMethods</c> maps the method id to the DECLARING type; a method a type gets from
///     an interface is declared by the highest type of the chain that implements the interface.
/// </summary>
public class CkMethodDeclaringElementResolverTests
{
    private const string Tenant = "meshtest";
    private const string Model = "Sample.Fleet";

    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly CkCacheMethodDeclaringElementResolver _resolver;

    public CkMethodDeclaringElementResolverTests()
    {
        _resolver = new CkCacheMethodDeclaringElementResolver(
            NullLogger<CkCacheMethodDeclaringElementResolver>.Instance, _cache);
        A.CallTo(() => _cache.IsTenantLoaded(Tenant)).Returns(true);

        // Vehicle declares Start-1; Car : Vehicle declares Honk-1; Truck : Vehicle.
        // Labeled is an interface with Relabel-1, implemented by Vehicle (so Vehicle declares it for Car and Truck).
        var start = Method("Vehicle", "Start-1");
        var relabel = Method("Vehicle", "Relabel-1");
        Register("Vehicle", start, relabel);
        Register("Car", start, relabel, Method("Car", "Honk-1"));
        Register("Truck", start, relabel);
        Register("Bike", Method("Bike", "Relabel-1")); // implements Labeled itself, no base: declares it
    }

    private static CkMethodGraph Method(string declaringType, string methodId) =>
        new(new CkId<CkTypeId>($"{Model}-1.0.0/{declaringType}"), new CkMethodDto { MethodId = methodId });

    private void Register(string type, params CkMethodGraph[] methods)
    {
        var graph = new CkTypeGraph(new CkId<CkTypeId>($"{Model}-1.0.0/{type}"), false, false, false, [], null, null, [],
            [], new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [], new CkGraphDirectedAssociations([]), "",
            false, allMethods: methods.ToDictionary(m => m.Definition.MethodId));
        var rtId = new RtCkId<CkTypeId>($"{Model}/{type}");
        A.CallTo(() => _cache.TryGetRtCkType(Tenant, rtId, out graph)).Returns(true);
    }

    private static Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods.CkMethodRequest Request(
        string methodId, string ckTypeId, string tenant = Tenant) =>
        new(tenant)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = methodId,
            CkTypeId = ckTypeId,
            ParametersJson = "{}",
            DeadlineUtc = DateTime.UtcNow.AddSeconds(5)
        };

    [Fact]
    public void MethodDeclaredOnTheTargetType_ResolvesToItself()
    {
        Assert.Equal($"{Model}/Car.Honk-1", _resolver.Resolve(Request($"{Model}/Car.Honk-1", $"{Model}/Car")));
    }

    [Theory]
    [InlineData("Car")]
    [InlineData("Truck")]
    public void InheritedMethod_ResolvesToTheBaseTypeThatDeclaresIt(string target)
    {
        // the caller addressed the method through the derived type
        Assert.Equal($"{Model}/Vehicle.Start-1",
            _resolver.Resolve(Request($"{Model}/{target}.Start-1", $"{Model}/{target}")));
    }

    [Theory]
    [InlineData("Car")]
    [InlineData("Truck")]
    public void InterfaceMethod_ResolvesToTheHighestImplementingType(string target)
    {
        Assert.Equal($"{Model}/Vehicle.Relabel-1",
            _resolver.Resolve(Request($"{Model}/{target}.Relabel-1", $"{Model}/{target}")));
    }

    [Fact]
    public void InterfaceMethod_OfATypeWithoutImplementingBase_ResolvesToThatType()
    {
        Assert.Equal($"{Model}/Bike.Relabel-1", _resolver.Resolve(Request($"{Model}/Bike.Relabel-1", $"{Model}/Bike")));
    }

    [Fact]
    public void ModelVersionOfTheRequestIsIgnored()
    {
        Assert.Equal($"{Model}/Vehicle.Start-1",
            _resolver.Resolve(Request($"{Model}-1.0.0/Car.Start-1", $"{Model}/Car")));
    }

    [Theory]
    [InlineData("Sample.Fleet/Car.Unknown-1", "Sample.Fleet/Car")]
    [InlineData("Sample.Fleet/Unknown.Start-1", "Sample.Fleet/Unknown")]
    [InlineData("Sample.Fleet/Car.Start-1", "not a type id")]
    [InlineData("garbage", "Sample.Fleet/Car")]
    public void UnknownTypeMethodOrMalformedId_IsRoutedAsGiven_AndNeverThrows(string methodId, string ckTypeId)
    {
        Assert.Equal(methodId, _resolver.Resolve(Request(methodId, ckTypeId)));
    }

    [Fact]
    public void TenantWithoutLoadedCkCache_IsRoutedAsGiven()
    {
        var request = Request($"{Model}/Car.Start-1", $"{Model}/Car", tenant: "other");

        Assert.Equal(request.MethodId, _resolver.Resolve(request));
    }

    [Fact]
    public void WithoutCkCache_IsRoutedAsGiven()
    {
        var resolver = new CkCacheMethodDeclaringElementResolver(NullLogger<CkCacheMethodDeclaringElementResolver>.Instance);

        Assert.Equal($"{Model}/Car.Start-1", resolver.Resolve(Request($"{Model}/Car.Start-1", $"{Model}/Car")));
    }

    [Fact]
    public void CacheFault_IsRoutedAsGiven()
    {
        A.CallTo(() => _cache.IsTenantLoaded(Tenant)).Throws<InvalidOperationException>();

        Assert.Equal($"{Model}/Car.Start-1", _resolver.Resolve(Request($"{Model}/Car.Start-1", $"{Model}/Car")));
    }

    [Fact]
    public void TheQueueOfTheResolvedIdIsTheQueueOfTheDeclaringElement()
    {
        var resolved = _resolver.Resolve(Request($"{Model}/Car.Start-1", $"{Model}/Car"));

        Assert.Equal(Contracts.DistributionEventHub.Methods.CkMethodQueueNames.For($"{Model}/Vehicle.Start-1"),
            Contracts.DistributionEventHub.Methods.CkMethodQueueNames.For(resolved));
        Assert.NotEqual(Contracts.DistributionEventHub.Methods.CkMethodQueueNames.For($"{Model}/Car.Start-1"),
            Contracts.DistributionEventHub.Methods.CkMethodQueueNames.For(resolved));
    }
}
