using System.Security.Claims;
using FakeItEasy;
using Infrastructure.Tests.TenantGeneration;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     AB#8999: <see cref="TenantAuthorizationMiddleware" /> refuses, in <c>Enforce</c>, an access token issued for an
///     earlier tenant with the same id (<c>tenant_gen</c>), and only logs and counts in <c>LogOnly</c>. The generation is
///     always the one of the token's <c>tenant_id</c>.
/// </summary>
public class TenantAuthorizationMiddlewareGenerationTests
{
    private const string Tenant = "meshtest";
    private const string Child = "childtenant";
    private const string OldGeneration = "1794750381540";
    private static readonly DateTime OldCreated = new(2026, 11, 15, 13, 46, 21, 540, DateTimeKind.Utc);
    private static readonly DateTime NewCreated = OldCreated.AddMinutes(5);
    private static readonly string NewGeneration = TenantGenerationFormat.Format(NewCreated)!;

    private static (bool NextCalled, HttpContext Context) Invoke(GenerationFixture f, IEnumerable<Claim> claims,
        string routeTenant = Tenant, bool parentEndpoint = false, ITenantHierarchyReader? hierarchy = null,
        TenantAuthorizationOptions? options = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer dummy-token";
        context.Request.RouteValues = new RouteValueDictionary { { InfrastructureCommon.TenantIdRoute, routeTenant } };
        if (parentEndpoint)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new AllowParentTenantAdministrationAttribute()), "backup (test)"));
        }

        context.RequestServices = new Services(f.Check, hierarchy);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }, Options.Create(options ?? f.Options), NullLogger<TenantAuthorizationMiddleware>.Instance);
        middleware.InvokeAsync(context).GetAwaiter().GetResult();
        return (nextCalled, context);
    }

    private static IEnumerable<Claim> User(string tenant, string? generation, string clientId = "studio")
    {
        yield return new Claim("sub", "6600000000000000000000ff");
        yield return new Claim("client_id", clientId);
        yield return new Claim("tenant_id", tenant);
        if (generation != null)
        {
            yield return new Claim(TenantGenerationClaim.ClaimType, generation);
        }
    }

    private static IEnumerable<Claim> Service(string tenant, string? generation, string clientId = "mesh-adapter")
    {
        yield return new Claim("client_id", clientId);
        yield return new Claim("tenant_id", tenant);
        if (generation != null)
        {
            yield return new Claim(TenantGenerationClaim.ClaimType, generation);
        }
    }

    private static GenerationFixture Recreated(TenantGenerationEnforcementMode mode)
    {
        var f = new GenerationFixture(mode);
        f.Lifecycle.Set(Tenant, NewCreated);
        return f;
    }

    [Theory]
    [InlineData(TenantGenerationEnforcementMode.LogOnly)]
    [InlineData(TenantGenerationEnforcementMode.Enforce)]
    public void MatchingGeneration_IsAllowed(TenantGenerationEnforcementMode mode)
    {
        using var f = Recreated(mode);

        var (next, ctx) = Invoke(f, User(Tenant, NewGeneration));

        Assert.True(next);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Empty(f.Logger.Warnings);
    }

    [Fact]
    public void Mismatch_LogOnly_IsAllowed_LoggedAndCounted()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.LogOnly);
        using var listener = new GenerationCheckListener(f.Metrics);

        var (next, _) = Invoke(f, User(Tenant, OldGeneration));

        Assert.True(next);
        Assert.Single(f.Logger.Warnings);
        var m = Assert.Single(listener.Measurements);
        Assert.Equal("mismatch", m.Tags["outcome"]);
        Assert.Equal("http", m.Tags["transport"]);
        Assert.Equal("log_only", m.Tags["mode"]);
    }

    [Fact]
    public void Mismatch_Enforce_Is403WithAReason()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.Enforce);

        var (next, ctx) = Invoke(f, User(Tenant, OldGeneration));

        Assert.False(next);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        var body = new StreamReader(ctx.Response.Body).ReadToEnd();
        Assert.Contains("earlier tenant", body);
        Assert.DoesNotContain(OldGeneration, body);
        Assert.DoesNotContain(NewGeneration, body);
    }

    [Fact]
    public void MissingClaim_LogOnly_IsAllowedAndCounted_Enforce_Is403()
    {
        using (var f = Recreated(TenantGenerationEnforcementMode.LogOnly))
        using (var listener = new GenerationCheckListener(f.Metrics))
        {
            Assert.True(Invoke(f, User(Tenant, null)).NextCalled);
            Assert.Equal("missing", Assert.Single(listener.Measurements).Tags["outcome"]);
        }

        using (var f = Recreated(TenantGenerationEnforcementMode.Enforce))
        {
            var (next, ctx) = Invoke(f, User(Tenant, null));
            Assert.False(next);
            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        }
    }

    [Fact]
    public void LookupFailure_Enforce_Is503_LogOnly_Proceeds()
    {
        using (var f = Recreated(TenantGenerationEnforcementMode.Enforce))
        {
            f.Lifecycle.Failure = new TimeoutException("mongo");
            var (next, ctx) = Invoke(f, User(Tenant, NewGeneration));
            Assert.False(next);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        }

        using (var f = Recreated(TenantGenerationEnforcementMode.LogOnly))
        using (var listener = new GenerationCheckListener(f.Metrics))
        {
            f.Lifecycle.Failure = new TimeoutException("mongo");
            Assert.True(Invoke(f, User(Tenant, NewGeneration)).NextCalled);
            Assert.Equal("lookup_failed", Assert.Single(listener.Measurements).Tags["outcome"]);
        }
    }

    [Fact]
    public void LegacyTenantWithoutRecord_IsNotChecked_InEnforce()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);

        Assert.True(Invoke(f, User(Tenant, null)).NextCalled);
    }

    [Fact]
    public void SystemTenantToken_WithMismatchingGeneration_IsNeverRefused()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Lifecycle.Set("octosystem", NewCreated);

        var (next, _) = Invoke(f, User("octosystem", OldGeneration), routeTenant: "octosystem");

        Assert.True(next);
        Assert.Equal(0, f.Lifecycle.Reads);
    }

    [Fact]
    public void OldClientCredentialsToken_OfRecreatedTenant_Is403InEnforce()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.Enforce);

        var (next, ctx) = Invoke(f, Service(Tenant, OldGeneration));

        Assert.False(next);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.True(Invoke(f, Service(Tenant, NewGeneration)).NextCalled);
    }

    [Fact]
    public void AllowListedCrossTenantClient_OnAnotherTenant_IsUnchanged_AndItsTokenTenantNotChecked()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        f.Options.CrossTenantServiceClientIds.Add("platform-worker");
        f.Lifecycle.Set("octosystem", NewCreated);
        f.Lifecycle.Set(Tenant, NewCreated);

        var (next, _) = Invoke(f, Service("octosystem", OldGeneration, "platform-worker"));

        Assert.True(next);
        Assert.Equal(0, f.Lifecycle.Reads);
    }

    [Fact]
    public void AllowListedClient_OnItsOwnTenant_IsChecked()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.Enforce);
        f.Options.CrossTenantServiceClientIds.Add("platform-worker");

        var (next, ctx) = Invoke(f, Service(Tenant, OldGeneration, "platform-worker"));

        Assert.False(next);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void ParentPath_StaleParentToken_IsRefusedInEnforce_AgainstTheParentGeneration()
    {
        using var f = new GenerationFixture(TenantGenerationEnforcementMode.Enforce);
        // the parent (token tenant) was recreated; the child was not
        f.Lifecycle.Set(Tenant, NewCreated);
        f.Lifecycle.Set(Child, OldCreated);
        var hierarchy = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => hierarchy.IsChildTenantAsync(Tenant, Child)).Returns(true);

        var (staleNext, staleCtx) = Invoke(f, User(Tenant, OldGeneration), routeTenant: Child, parentEndpoint: true,
            hierarchy: hierarchy);
        Assert.False(staleNext);
        Assert.Equal(StatusCodes.Status403Forbidden, staleCtx.Response.StatusCode);

        var (freshNext, _) = Invoke(f, User(Tenant, NewGeneration), routeTenant: Child, parentEndpoint: true,
            hierarchy: hierarchy);
        Assert.True(freshNext);
    }

    [Fact]
    public void Disabled_NeverLooksUp()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.Disabled);

        Assert.True(Invoke(f, User(Tenant, OldGeneration)).NextCalled);
        Assert.Equal(0, f.Lifecycle.Reads);
    }

    [Fact]
    public async Task HostWithoutTheCheck_BehavesAsBefore()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer dummy-token";
        context.Request.RouteValues = new RouteValueDictionary { { InfrastructureCommon.TenantIdRoute, Tenant } };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(User(Tenant, OldGeneration), "Bearer"));
        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, Options.Create(new TenantAuthorizationOptions
        {
            TenantGenerationEnforcement = TenantGenerationEnforcementMode.Enforce
        }), NullLogger<TenantAuthorizationMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task CookiePrincipal_IsNotChecked()
    {
        using var f = Recreated(TenantGenerationEnforcementMode.Enforce);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer injected-from-cookie";
        context.Request.RouteValues = new RouteValueDictionary { { InfrastructureCommon.TenantIdRoute, Tenant } };
        context.RequestServices = new Services(f.Check, null);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(User(Tenant, null), "Identity.Application"));
        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, Options.Create(f.Options), NullLogger<TenantAuthorizationMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(0, f.Lifecycle.Reads);
    }

    private sealed class Services(ITenantGenerationCheck check, ITenantHierarchyReader? hierarchy) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ITenantGenerationCheck))
            {
                return check;
            }

            return serviceType == typeof(ITenantHierarchyReader) ? hierarchy : null;
        }
    }
}
