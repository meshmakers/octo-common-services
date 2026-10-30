using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>
///     AB#7912: the CK cache keys tenants by the lower-case tenant id (the database name), while
///     <c>OctoSystemConfiguration.SystemTenantId</c> is mixed-case ("OctoSystem"). The start-up validation must look the
///     methods up with the cache key, otherwise a host with the default configuration refuses to start.
/// </summary>
public class CkCacheMethodDefinitionSourceTests
{
    private const string CacheKey = "octosystem";

    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();

    public CkCacheMethodDefinitionSourceTests()
    {
        // Behaves like CkCacheService: a case-sensitive dictionary that only knows the lower-case key.
        A.CallTo(() => _cache.IsTenantLoaded(A<string>._)).Returns(false);
        A.CallTo(() => _cache.IsTenantLoaded(CacheKey)).Returns(true);

        var definition = TestData.Definition();
        var graph = new CkTypeGraph(new CkId<CkTypeId>("System.Identity-1.0.0/User"), false, false, false, [], null, null,
            [], [], new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [], new CkGraphDirectedAssociations([]),
            "", false, allMethods: new Dictionary<string, CkMethodGraph>
            {
                [definition.MethodId] = new(new CkId<CkTypeId>("System.Identity-1.0.0/User"), definition)
            });
        var rtId = new RtCkId<CkTypeId>("System.Identity/User");
        A.CallTo(() => _cache.TryGetRtCkType(CacheKey, rtId, out graph)).Returns(true);
    }

    private CkCacheMethodDefinitionSource CreateSource(string systemTenantId, params string[] validationTenantIds)
    {
        var options = new CkMethodHandlerOptions { ValidationTenantIds = validationTenantIds.ToList() };
        var system = new OctoSystemConfiguration { SystemTenantId = systemTenantId };
        return new CkCacheMethodDefinitionSource(_cache, Options.Create(options), Options.Create(system));
    }

    [Fact]
    public void DefaultConfiguration_MixedCaseSystemTenant_FindsTheDeclaredMethod()
    {
        var source = CreateSource("OctoSystem");

        Assert.Equal([CacheKey], source.TenantIds);
        Assert.NotNull(source.Find(TestData.MethodId));
    }

    [Fact]
    public void DefaultConfiguration_HostWithAHandlerForADeclaredMethod_PassesStartupValidation()
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        registrations.Add(TestData.MethodId, typeof(ChangePasswordHandler));

        new CkMethodRegistrationValidator(CreateSource("OctoSystem")).Validate(registrations);

        Assert.NotNull(registrations.Handlers[TestData.MethodId].Definition);
    }

    [Fact]
    public void ConfiguredValidationTenants_AreNormalisedToo()
    {
        var source = CreateSource("Other", " OctoSystem ");

        Assert.Equal([CacheKey], source.TenantIds);
        Assert.NotNull(source.Find(TestData.MethodId));
    }

    [Fact]
    public void UnknownMethod_IsStillNotFound()
    {
        Assert.Null(CreateSource("OctoSystem").Find("System.Identity/User.Nope-1"));
    }
}
