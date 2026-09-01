using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     Unit tests for <see cref="TenantAuthorizationMiddleware" />, focused on AB#5032: narrowing the
///     tenant-check exemption for client-credentials ("service") tokens.
///     Before AB#5032 any token without a <c>sub</c> claim skipped the tenant check entirely, so —
///     with <c>ValidateAudience = false</c> — every client-credentials client of the authority could
///     address every tenant. The new behaviour is staged behind
///     <see cref="TenantAuthorizationOptions.ServiceTokenEnforcement" /> whose default is
///     <see cref="ServiceTokenTenantEnforcementMode.LogOnly" /> = unchanged request outcomes.
/// </summary>
public class TenantAuthorizationMiddlewareTests
{
    private const string RouteTenant = "meshtest";
    private const string ForeignTenant = "othertenant";

    private static HttpContext CreateContext(IEnumerable<Claim> claims, string? routeTenantId = RouteTenant,
        bool withBearerHeader = true)
    {
        var httpContext = new DefaultHttpContext();
        if (withBearerHeader)
        {
            httpContext.Request.Headers.Authorization = "Bearer dummy-token";
        }

        if (routeTenantId != null)
        {
            httpContext.Request.RouteValues = new RouteValueDictionary
            {
                { InfrastructureCommon.TenantIdRoute, routeTenantId }
            };
        }

        // "Bearer" is the AuthenticationType the middleware expects for the JWT path.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return httpContext;
    }

    private static (bool NextCalled, HttpContext Context, RecordingLogger Logger) Invoke(
        HttpContext context, TenantAuthorizationOptions? options = null)
    {
        var logger = new RecordingLogger();
        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            Options.Create(options ?? new TenantAuthorizationOptions()),
            logger);

        middleware.InvokeAsync(context).GetAwaiter().GetResult();
        return (nextCalled, context, logger);
    }

    private static IEnumerable<Claim> ServiceToken(string clientId, string? tenantId)
    {
        yield return new Claim("client_id", clientId);
        if (tenantId != null)
        {
            yield return new Claim("tenant_id", tenantId);
        }
    }

    private static IEnumerable<Claim> UserToken(string? tenantId, string? clientId = null)
    {
        yield return new Claim("sub", "6600000000000000000000ff");
        if (tenantId != null)
        {
            yield return new Claim("tenant_id", tenantId);
        }

        if (clientId != null)
        {
            yield return new Claim("client_id", clientId);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Service token, matching tenant — allowed in every mode.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.Disabled)]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly)]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce)]
    public void ServiceToken_WithMatchingTenant_IsAllowed(ServiceTokenTenantEnforcementMode mode)
    {
        var context = CreateContext(ServiceToken("octo-pipeline-sa-1", RouteTenant));

        var (nextCalled, ctx, logger) = Invoke(context,
            new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ServiceToken_WithMatchingTenant_IsCaseInsensitive()
    {
        var context = CreateContext(ServiceToken("octo-pipeline-sa-1", "MeshTest"));

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Service token, foreign tenant.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ServiceToken_WithForeignTenant_IsDeniedWhenEnforcing()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("some-ci-client", warning);
        Assert.Contains(ForeignTenant, warning);
        Assert.Contains(RouteTenant, warning);
    }

    [Fact]
    public void ServiceToken_WithForeignTenant_IsLoggedButAllowedByDefault()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        // No options at all == production default.
        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("some-ci-client", warning);
        Assert.Contains(ForeignTenant, warning);
        Assert.Contains(RouteTenant, warning);
    }

    [Fact]
    public void ServiceToken_WithForeignTenant_IsSilentlyAllowedWhenDisabled()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Disabled
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
    }

    // ---------------------------------------------------------------------------------------------
    // Service token without a tenant_id claim — the shape every client-credentials token had before
    // AB#5032. Fail closed when enforcing, log the inventory entry otherwise.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ServiceToken_WithoutTenantClaim_IsDeniedWhenEnforcing()
    {
        var context = CreateContext(ServiceToken("legacy-client", null));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains("legacy-client", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void ServiceToken_WithoutTenantClaim_IsLoggedButAllowedByDefault()
    {
        var context = CreateContext(ServiceToken("legacy-client", null));

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("legacy-client", warning);
        Assert.Contains(RouteTenant, warning);
    }

    // ---------------------------------------------------------------------------------------------
    // Allow-list — the regression guard for the genuinely multi-tenant platform workers
    // (AI adapter worker, mesh adapter): they must keep reaching every tenant.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AllowListedServiceToken_WithForeignTenant_IsAllowedWhenEnforcing()
    {
        var context = CreateContext(ServiceToken("octo-ai-adapter", ForeignTenant));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            CrossTenantServiceClientIds = { "octo-ai-adapter", "octo-mesh-adapter" }
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void AllowListedServiceToken_WithoutTenantClaim_IsAllowedWhenEnforcing()
    {
        var context = CreateContext(ServiceToken("octo-mesh-adapter", null));

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            CrossTenantServiceClientIds = { "octo-mesh-adapter" }
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void AllowList_MatchesCaseInsensitivelyAndByPrefixPattern()
    {
        var options = new TenantAuthorizationOptions
        {
            CrossTenantServiceClientIds = { "Octo-Mesh-Adapter", "octo-worker-*" }
        };

        Assert.True(options.IsCrossTenantServiceClient("octo-mesh-adapter"));
        Assert.True(options.IsCrossTenantServiceClient("OCTO-MESH-ADAPTER"));
        Assert.True(options.IsCrossTenantServiceClient("octo-worker-ai"));
        Assert.False(options.IsCrossTenantServiceClient("octo-worker"));
        Assert.False(options.IsCrossTenantServiceClient("other"));
        Assert.False(options.IsCrossTenantServiceClient(null));
        Assert.False(options.IsCrossTenantServiceClient(string.Empty));
    }

    // ---------------------------------------------------------------------------------------------
    // User tokens are untouched by AB#5032 and staged separately since AB#5054 — with the opposite
    // default: Enforce, so no host where the check is live today is weakened by the option existing.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UserToken_WithMatchingTenant_IsAllowed()
    {
        var context = CreateContext(UserToken(RouteTenant));

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.Disabled)]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly)]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce)]
    public void UserToken_WithForeignTenant_IsAlwaysDenied(ServiceTokenTenantEnforcementMode mode)
    {
        var context = CreateContext(UserToken(ForeignTenant));

        var (nextCalled, ctx, _) = Invoke(context,
            new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void UserToken_MappedSubjectClaim_IsStillTreatedAsUserToken()
    {
        // MapInboundClaims=true renames "sub" to ClaimTypes.NameIdentifier. Such a token must not be
        // mistaken for a service token — otherwise the allow-list would apply to users.
        var context = CreateContext(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "6600000000000000000000ff"),
            new Claim("tenant_id", ForeignTenant)
        });

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Disabled
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void UserToken_WithoutTenantClaim_IsDeniedByDefault()
    {
        // A user token that cannot be attributed to a tenant fails closed — the shape
        // octo-mcp-service's RuntimeSecurityContextResolver mirrors for its per-tool tenant gate.
        var context = CreateContext(UserToken(null));

        var (nextCalled, ctx, _) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void UserToken_WithMatchingTenant_IsNeverLoggedWhenStaging()
    {
        var context = CreateContext(UserToken(RouteTenant));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void UserToken_WithForeignTenant_IsLoggedButAllowedWhenStaging()
    {
        var context = CreateContext(UserToken(ForeignTenant, "octo-meshmakers-app"));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        // The client id is the actionable half of the inventory: it names the application to fix.
        Assert.Contains("octo-meshmakers-app", warning);
        Assert.Contains(ForeignTenant, warning);
        Assert.Contains(RouteTenant, warning);
    }

    [Fact]
    public void UserToken_WithoutTenantClaim_IsLoggedButAllowedWhenStaging()
    {
        var context = CreateContext(UserToken(null));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void UserTokenStaging_DoesNotLoosenTheServiceTokenPath()
    {
        // The two staged paths are independent: a host that opts its user path down to LogOnly for
        // the AB#5054 migration must not thereby re-open the AB#5032 service-token exemption.
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Untouched pass-through paths.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RouteWithoutTenant_IsAllowedForServiceTokens()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant), routeTenantId: null);

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void RequestWithoutBearerHeader_IsAllowed()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant), withBearerHeader: false);

        var (nextCalled, ctx, _) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public void UnauthenticatedRequest_IsAllowedThroughToTheAuthMiddleware()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer dummy-token";
        httpContext.Request.RouteValues = new RouteValueDictionary
        {
            { InfrastructureCommon.TenantIdRoute, RouteTenant }
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var (nextCalled, ctx, _) = Invoke(httpContext, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    /// <summary>
    ///     Minimal logger that records the rendered message of every warning, so the tests can assert
    ///     that the audit line actually names the client and both tenants.
    /// </summary>
    private sealed class RecordingLogger : ILogger<TenantAuthorizationMiddleware>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
