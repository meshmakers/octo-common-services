using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    private const string ParentTenant = "parenttenant";

    private static HttpContext CreateContext(IEnumerable<Claim> claims, string? routeTenantId = RouteTenant,
        bool withBearerHeader = true, bool parentTenantAdministrationEndpoint = false,
        ITenantHierarchyReader? hierarchyReader = null)
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

        // The endpoint stands in for the tenant-administration routes of AB#5060 (backup, restore,
        // archive export). The marker is what opts an endpoint into the parent-tenant rule; the
        // middleware reads it off the endpoint metadata, exactly like [AllowAnonymous].
        if (parentTenantAdministrationEndpoint)
        {
            httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new AllowParentTenantAdministrationAttribute()),
                "POST /{tenantId}/v1/tenants/backup (test endpoint)"));
        }

        if (hierarchyReader != null)
        {
            httpContext.RequestServices = new SingleServiceProvider(hierarchyReader);
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

    /// <summary>
    ///     🔴 AB#5077 reversed this: the production default is <c>Enforce</c>, so a service token on a
    ///     foreign tenant is refused when nobody configures anything. Asserted with no options at all,
    ///     because "what an unconfigured host does" is the whole point of the default.
    /// </summary>
    [Fact]
    public void ServiceToken_WithForeignTenant_IsDeniedByDefault()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        // No options at all == production default.
        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("some-ci-client", warning);
        Assert.Contains(ForeignTenant, warning);
        Assert.Contains(RouteTenant, warning);
    }

    /// <summary>
    ///     The migration mode still exists and still lets the request through — it is just no longer
    ///     the default. An environment that still needs its consumer inventory opts down explicitly.
    /// </summary>
    [Fact]
    public void ServiceToken_WithForeignTenant_IsLoggedButAllowedWhenLogOnly()
    {
        var context = CreateContext(ServiceToken("some-ci-client", ForeignTenant));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.LogOnly
        });

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

    /// <summary>
    ///     🔴 AB#5077: a token with no <c>tenant_id</c> at all — the shape every client-credentials
    ///     token had before AB#5032 — is now refused by default, i.e. fail closed. This is the case
    ///     most likely to bite on rollout, because such a token is issued by any client-credentials
    ///     login that omits <c>acr_values</c>, and nothing about it fails at build time.
    /// </summary>
    [Fact]
    public void ServiceToken_WithoutTenantClaim_IsDeniedByDefault()
    {
        var context = CreateContext(ServiceToken("legacy-client", null));

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("legacy-client", warning);
        Assert.Contains(RouteTenant, warning);
    }

    [Fact]
    public void ServiceToken_WithoutTenantClaim_IsLoggedButAllowedWhenLogOnly()
    {
        var context = CreateContext(ServiceToken("legacy-client", null));

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.LogOnly
        });

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

    // ---------------------------------------------------------------------------------------------
    // AB#5060 — the parent-tenant administration rule. A user token may address a tenant BELOW its
    // own, but only on an endpoint that is explicitly marked as administering that tenant. A parent
    // administrator must NOT thereby reach the child's data routes, which is why every test here
    // pairs the granted case with the identical request on an unmarked endpoint.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void UserTokenOfParentTenant_MayAdministerChildTenant_OnAMarkedEndpoint()
    {
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(UserToken(ParentTenant, "octo-cli"),
            parentTenantAdministrationEndpoint: true, hierarchyReader: reader);

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal(1, reader.Calls);

        // The grant is recorded so an operator can see who actually relies on the rule.
        var info = Assert.Single(logger.Informations);
        Assert.Contains("octo-cli", info);
        Assert.Contains(ParentTenant, info);
        Assert.Contains(RouteTenant, info);
        // …and it is not an inventory entry: nothing here would be denied.
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void UserTokenOfParentTenant_IsDeniedOnAnUnmarkedEndpoint_WithoutAskingTheHierarchy()
    {
        // The data routes of the child tenant are exactly what a parent administrator must not get.
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(UserToken(ParentTenant, "octo-cli"), hierarchyReader: reader);

        var (nextCalled, ctx, _) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public void UserTokenOfUnrelatedTenant_IsDeniedEvenOnAMarkedEndpoint()
    {
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(UserToken(ForeignTenant, "octo-cli"),
            parentTenantAdministrationEndpoint: true, hierarchyReader: reader);

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal(1, reader.Calls);
        Assert.Empty(logger.Informations);
    }

    [Fact]
    public void UserTokenOfTheSameTenant_NeverResolvesTheHierarchy()
    {
        // Equality is the common case: it must be answered before any hierarchy lookup, even on an
        // endpoint that opts into the rule. Asserted through the reader's call counter rather than by
        // reading the code.
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(UserToken(RouteTenant), parentTenantAdministrationEndpoint: true,
            hierarchyReader: reader);

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal(0, reader.Calls);
        Assert.Empty(logger.Informations);
    }

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly)]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce)]
    public void ServiceTokenOfParentTenant_IsNeverAllowedByTheParentTenantRule(
        ServiceTokenTenantEnforcementMode mode)
    {
        // A client-credentials token's tenant_id proves nothing (mirrored clients share the parent's
        // secret; a token minted without acr_values claims the system tenant, i.e. the root of the
        // hierarchy). The rule must never look at one — not even on a marked endpoint.
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(ServiceToken("some-ci-client", ParentTenant),
            parentTenantAdministrationEndpoint: true, hierarchyReader: reader);

        var (nextCalled, ctx, _) = Invoke(context,
            new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        Assert.Equal(0, reader.Calls);
        if (mode == ServiceTokenTenantEnforcementMode.Enforce)
        {
            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        }
        else
        {
            // LogOnly lets it through as before AB#5032 — but as an inventory entry, not as a grant.
            Assert.True(nextCalled);
        }
    }

    [Fact]
    public void ParentTenantGrant_IsNotListedAsAnInventoryEntryWhenStaging()
    {
        // In LogOnly the rule still runs first: an access it grants would NOT be denied by an
        // enforcing run, so listing it would poison the AB#5054 inventory.
        var reader = new CountingHierarchyReader(ParentTenant, RouteTenant);
        var context = CreateContext(UserToken(ParentTenant, "octo-cli"),
            parentTenantAdministrationEndpoint: true, hierarchyReader: reader);

        var (nextCalled, ctx, logger) = Invoke(context, new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        });

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Empty(logger.Warnings);
        Assert.Single(logger.Informations);
    }

    [Fact]
    public void MarkedEndpoint_WithoutAHierarchyReader_FailsClosed()
    {
        // A host that marks an endpoint but never registered the reader must not silently widen
        // anything — it falls back to the exact match and says so.
        var context = CreateContext(UserToken(ParentTenant, "octo-cli"),
            parentTenantAdministrationEndpoint: true);

        var (nextCalled, ctx, logger) = Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains(nameof(ITenantHierarchyReader), Assert.Single(logger.Warnings));
    }

    /// <summary>
    ///     Records how often the middleware asks for a hierarchy answer, so the tests can prove that
    ///     the equality case and the unmarked endpoints never trigger a resolution.
    /// </summary>
    private sealed class CountingHierarchyReader(string parentTenantId, string childTenantId)
        : ITenantHierarchyReader
    {
        public int Calls { get; private set; }

        public Task<bool> IsChildTenantAsync(string parent, string tenantId)
        {
            Calls++;
            return Task.FromResult(
                string.Equals(parent, parentTenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tenantId, childTenantId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    ///     The request services of a host that registered the hierarchy reader; everything else is
    ///     unresolvable, like in a middleware unit test.
    /// </summary>
    private sealed class SingleServiceProvider(ITenantHierarchyReader reader) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ITenantHierarchyReader) ? reader : null;
    }

    /// <summary>
    ///     Minimal logger that records the rendered message of every warning, so the tests can assert
    ///     that the audit line actually names the client and both tenants. Information is recorded
    ///     separately: that is where the AB#5060 grants are logged, and a grant must never show up
    ///     among the warnings (it is not an inventory entry).
    /// </summary>
    private sealed class RecordingLogger : ILogger<TenantAuthorizationMiddleware>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Informations { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Information)
            {
                Informations.Add(formatter(state, exception));
            }
        }
    }
}
