using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Routing;

/// <summary>
///     The shared <c>{tenantId:tenantId}</c> constraint (AB#5060), consolidated from the seven
///     per-host copies that had drifted apart.
/// </summary>
/// <remarks>
///     What matters here is the <b>boundary</b>, not the happy path: the constraint decides whether a
///     path segment travels on into services that will use it as a Mongo database name, a cache key
///     or a directory name. Five of the seven copies accepted any non-null value, which is why the
///     rejection cases below carry the weight.
/// </remarks>
public class TenantIdRouteConstraintTests
{
    private static bool Match(object? value)
    {
        var constraint = new TenantIdRouteConstraint();
        var values = new RouteValueDictionary { [InfrastructureCommon.TenantIdRoute] = value };
        return constraint.Match(null, null, InfrastructureCommon.TenantIdRoute, values,
            RouteDirection.IncomingRequest);
    }

    [Theory]
    [InlineData("octosystem")]
    [InlineData("salzburgdev")]
    [InlineData("tenant-1")]
    [InlineData("tenant_1")]
    [InlineData("Tenant1")]
    [InlineData("a")]
    [InlineData("0")]
    public void AcceptsAWellFormedTenantId(string tenantId)
    {
        Assert.True(Match(tenantId));
    }

    /// <summary>
    ///     🔴 Every one of these was accepted by five of the seven previous copies. The dot segments
    ///     are the ones that reach a filesystem, the whitespace and separator cases the ones that
    ///     reach a database name.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("tenant 1")]
    [InlineData("tenant/1")]
    [InlineData("tenant\\1")]
    [InlineData("tenant.1")]
    [InlineData("tenant:1")]
    [InlineData("tenant$1")]
    [InlineData("tenäant")]
    [InlineData("tenant\n1")]
    public void RejectsASegmentThatCannotNameATenant(string tenantId)
    {
        Assert.False(Match(tenantId));
    }

    [Fact]
    public void RejectsAMissingRouteValue()
    {
        var constraint = new TenantIdRouteConstraint();
        var values = new RouteValueDictionary();

        Assert.False(constraint.Match(null, null, InfrastructureCommon.TenantIdRoute, values,
            RouteDirection.IncomingRequest));
    }

    [Fact]
    public void RejectsANullRouteValue()
    {
        Assert.False(Match(null));
    }

    /// <summary>
    ///     🔴 The length limit is shared with tenant creation
    ///     (<c>TenantContext.ValidateTenantIdFormat</c>, 64). The two are separate constants in
    ///     separate assemblies on purpose — this one must not drag in the runtime engine — so the
    ///     boundary is pinned here: raise it there without raising it here and real tenants 404;
    ///     raise it here alone and the constraint stops matching the rule it claims to mirror.
    /// </summary>
    [Fact]
    public void AcceptsUpToSixtyFourCharactersAndNoMore()
    {
        Assert.Equal(64, TenantIdRouteConstraint.MaxTenantIdLength);
        Assert.True(Match(new string('a', 64)));
        Assert.False(Match(new string('a', 65)));
    }

    /// <summary>
    ///     The registration is half the contract: a host that calls it can write
    ///     <c>{tenantId:tenantId}</c>, and one that does not fails at startup rather than silently
    ///     matching anything.
    /// </summary>
    [Fact]
    public void AddOctoTenantIdRouteConstraint_RegistersTheConstraintUnderTheExpectedKey()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        services.AddOctoTenantIdRouteConstraint();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RouteOptions>>().Value;

        Assert.True(options.ConstraintMap.TryGetValue(InfrastructureCommon.TenantIdRoute, out var registered));
        Assert.Equal(typeof(TenantIdRouteConstraint), registered);
    }
}
