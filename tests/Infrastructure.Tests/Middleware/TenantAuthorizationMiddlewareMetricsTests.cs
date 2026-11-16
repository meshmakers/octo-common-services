using System.Diagnostics.Metrics;
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
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     <see cref="TenantAuthorizationMiddleware" /> counts every decision of its tenant check in
///     <c>octo.tenant_auth.decisions</c>, one test per outcome, with the labels operators filter on and nothing that
///     identifies a tenant, a user or a token.
/// </summary>
public sealed class TenantAuthorizationMiddlewareMetricsTests : IDisposable
{
    private const string Tenant = "meshtest";
    private const string Foreign = "othertenant";
    private const string Child = "childtenant";
    private const string Subject = "6600000000000000000000ff";
    private const string RouteTemplate = "{tenantId}/v1/tenants/backup";

    private readonly TenantAuthorizationMetrics _metrics = new();
    private readonly DecisionListener _listener;

    public TenantAuthorizationMiddlewareMetricsTests()
    {
        _listener = new DecisionListener(_metrics);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // User tokens
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(UserTokenTenantEnforcementMode.Enforce, "enforce")]
    [InlineData(UserTokenTenantEnforcementMode.LogOnly, "log_only")]
    public void UserToken_MatchingTenant_IsCountedAsMatch(UserTokenTenantEnforcementMode mode, string modeLabel)
    {
        Invoke(UserToken(Tenant), new TenantAuthorizationOptions { UserTokenEnforcement = mode });

        var tags = SingleDecision();
        Assert.Equal("match", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal("user", tags[TenantAuthorizationMetrics.TokenKindTag]);
        Assert.Equal(modeLabel, tags[TenantAuthorizationMetrics.ModeTag]);
        Assert.Equal("studio", tags[TenantAuthorizationMetrics.ClientIdTag]);
        Assert.Equal(TenantAuthorizationMetrics.AnyRoute, tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void UserToken_ChildTenantOnParentAdministrationEndpoint_IsCountedAsAllowedParentAdmin()
    {
        var hierarchy = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => hierarchy.IsChildTenantAsync(Tenant, Child)).Returns(true);

        var (nextCalled, _) = Invoke(UserToken(Tenant), routeTenant: Child, parentEndpoint: true,
            hierarchy: hierarchy);

        Assert.True(nextCalled);
        Assert.Equal("allowed_parent_admin", SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    [Fact]
    public void UserToken_ForeignTenantInLogOnly_IsCountedAsWouldDeny()
    {
        var (nextCalled, _) = Invoke(UserToken(Tenant), new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        }, routeTenant: Foreign);

        Assert.True(nextCalled);
        var tags = SingleDecision();
        Assert.Equal("would_deny", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal("log_only", tags[TenantAuthorizationMetrics.ModeTag]);
        Assert.Equal("/" + RouteTemplate, tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void UserToken_ForeignTenantInEnforce_IsCountedAsDenied()
    {
        var (nextCalled, context) = Invoke(UserToken(Tenant), routeTenant: Foreign);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var tags = SingleDecision();
        Assert.Equal("denied", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal("enforce", tags[TenantAuthorizationMetrics.ModeTag]);
        Assert.Equal("/" + RouteTemplate, tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void UserToken_NotAChildOnParentAdministrationEndpoint_IsCountedAsDenied()
    {
        var hierarchy = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => hierarchy.IsChildTenantAsync(Tenant, Foreign)).Returns(false);

        var (nextCalled, _) = Invoke(UserToken(Tenant), routeTenant: Foreign, parentEndpoint: true,
            hierarchy: hierarchy);

        Assert.False(nextCalled);
        Assert.Equal("denied", SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    [Theory]
    [InlineData(UserTokenTenantEnforcementMode.LogOnly, "would_deny_missing_tenant_id")]
    [InlineData(UserTokenTenantEnforcementMode.Enforce, "denied_missing_tenant_id")]
    public void UserToken_WithoutTenantId_IsCountedAsMissingTenantId(UserTokenTenantEnforcementMode mode,
        string outcome)
    {
        Invoke(UserToken(null), new TenantAuthorizationOptions { UserTokenEnforcement = mode });

        Assert.Equal(outcome, SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    // ---------------------------------------------------------------------------------------------
    // Service tokens
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly, "log_only")]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce, "enforce")]
    public void ServiceToken_MatchingTenant_IsCountedAsMatch(ServiceTokenTenantEnforcementMode mode,
        string modeLabel)
    {
        Invoke(ServiceToken("mesh-adapter", Tenant), new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        var tags = SingleDecision();
        Assert.Equal("match", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal("service", tags[TenantAuthorizationMetrics.TokenKindTag]);
        Assert.Equal(modeLabel, tags[TenantAuthorizationMetrics.ModeTag]);
        Assert.Equal("mesh-adapter", tags[TenantAuthorizationMetrics.ClientIdTag]);
    }

    [Fact]
    public void ServiceToken_InDisabledMode_IsCountedAsNotChecked()
    {
        var (nextCalled, _) = Invoke(ServiceToken("mesh-adapter", Foreign), new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Disabled
        });

        Assert.True(nextCalled);
        var tags = SingleDecision();
        Assert.Equal("not_checked", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal("disabled", tags[TenantAuthorizationMetrics.ModeTag]);
    }

    [Fact]
    public void ServiceToken_OfAllowListedClient_IsCountedAsAllowedCrossTenantClient()
    {
        var (nextCalled, _) = Invoke(ServiceToken("ai-worker", "systemtenant"), new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            CrossTenantServiceClientIds = ["ai-worker"]
        });

        Assert.True(nextCalled);
        var tags = SingleDecision();
        Assert.Equal("allowed_cross_tenant_client", tags[TenantAuthorizationMetrics.OutcomeTag]);
        Assert.Equal(TenantAuthorizationMetrics.AnyRoute, tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void ServiceToken_OfAllowListedClientOnItsOwnTenant_IsCountedAsMatch()
    {
        var (nextCalled, _) = Invoke(ServiceToken("ai-worker", Tenant), new TenantAuthorizationOptions
        {
            ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce,
            CrossTenantServiceClientIds = ["ai-worker"]
        });

        Assert.True(nextCalled);
        Assert.Equal("match", SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly, "would_deny", true)]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce, "denied", false)]
    public void ServiceToken_ForeignTenant_IsCountedByMode(ServiceTokenTenantEnforcementMode mode, string outcome,
        bool passes)
    {
        var (nextCalled, _) = Invoke(ServiceToken("mesh-adapter", Foreign),
            new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        Assert.Equal(passes, nextCalled);
        Assert.Equal(outcome, SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly, "would_deny_missing_tenant_id", true)]
    [InlineData(ServiceTokenTenantEnforcementMode.Enforce, "denied_missing_tenant_id", false)]
    public void ServiceToken_WithoutTenantId_IsCountedByMode(ServiceTokenTenantEnforcementMode mode, string outcome,
        bool passes)
    {
        var (nextCalled, _) = Invoke(ServiceToken("mesh-adapter", null),
            new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        Assert.Equal(passes, nextCalled);
        Assert.Equal(outcome, SingleDecision()[TenantAuthorizationMetrics.OutcomeTag]);
    }

    // ---------------------------------------------------------------------------------------------
    // Labels and scope
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Decision_CarriesExactlyTheDocumentedLabels_AndNoTenantUserOrPath()
    {
        Invoke(UserToken(Tenant), new TenantAuthorizationOptions
        {
            UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly
        }, routeTenant: Foreign);

        var tags = SingleDecision();
        Assert.Equal(
            [
                TenantAuthorizationMetrics.ClientIdTag, TenantAuthorizationMetrics.ModeTag,
                TenantAuthorizationMetrics.OutcomeTag, TenantAuthorizationMetrics.RouteTag,
                TenantAuthorizationMetrics.TokenKindTag
            ],
            tags.Keys.Order(StringComparer.Ordinal));
        foreach (var value in tags.Values)
        {
            Assert.DoesNotContain(Tenant, value!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Foreign, value!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Subject, value!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Decision_WithoutClientIdOrRouteTemplate_UsesUnknown()
    {
        var context = CreateContext(new[] { new Claim("sub", Subject), new Claim("tenant_id", Tenant) }, Foreign,
            routeEndpoint: false);

        Run(context, new TenantAuthorizationOptions());

        var tags = SingleDecision();
        Assert.Equal(TenantGenerationMetrics.UnknownClientId, tags[TenantAuthorizationMetrics.ClientIdTag]);
        Assert.Equal(TenantAuthorizationMetrics.UnknownRoute, tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void RouteLabel_OverlongTemplate_IsOther()
    {
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("{tenantId}/" + new string('a', 200)), 0, EndpointMetadataCollection.Empty,
            "long");

        Assert.Equal(TenantAuthorizationMetrics.OtherRoute, TenantAuthorizationMetrics.RouteLabel(endpoint));
    }

    [Fact]
    public void SeriesBudget_Exhausted_CollapsesClientAndRoute_ButKeepsTheOutcome()
    {
        using var metrics = new TenantAuthorizationMetrics(maxSeries: 2);
        using var listener = new DecisionListener(metrics);
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(RouteTemplate), 0,
            EndpointMetadataCollection.Empty, "backup (test)");

        metrics.RecordUserToken(TenantAuthorizationOutcome.WouldDeny, UserTokenTenantEnforcementMode.LogOnly, "app-1",
            endpoint);
        metrics.RecordUserToken(TenantAuthorizationOutcome.Match, UserTokenTenantEnforcementMode.LogOnly, "app-2",
            endpoint);
        metrics.RecordUserToken(TenantAuthorizationOutcome.WouldDeny, UserTokenTenantEnforcementMode.LogOnly, "app-3",
            endpoint);
        metrics.RecordUserToken(TenantAuthorizationOutcome.Match, UserTokenTenantEnforcementMode.LogOnly, "app-4",
            endpoint);
        metrics.RecordUserToken(TenantAuthorizationOutcome.WouldDeny, UserTokenTenantEnforcementMode.LogOnly, "app-1",
            endpoint);

        var measurements = listener.Measurements.Select(m => m.Tags).ToList();
        Assert.Equal(5, measurements.Count);
        Assert.Equal(("would_deny", "app-1", "/" + RouteTemplate), Labels(measurements[0]));
        Assert.Equal(("match", "app-2", TenantAuthorizationMetrics.AnyRoute), Labels(measurements[1]));
        Assert.Equal(("would_deny", TenantGenerationMetrics.OtherClientId, TenantAuthorizationMetrics.OtherRoute),
            Labels(measurements[2]));
        Assert.Equal(("match", TenantGenerationMetrics.OtherClientId, TenantAuthorizationMetrics.AnyRoute),
            Labels(measurements[3]));
        // A combination admitted before the budget ran out keeps its labels.
        Assert.Equal(("would_deny", "app-1", "/" + RouteTemplate), Labels(measurements[4]));
        Assert.All(measurements, m => Assert.Equal("user", m[TenantAuthorizationMetrics.TokenKindTag]));

        static (string?, string?, string?) Labels(Dictionary<string, string?> tags) =>
            (tags[TenantAuthorizationMetrics.OutcomeTag], tags[TenantAuthorizationMetrics.ClientIdTag],
                tags[TenantAuthorizationMetrics.RouteTag]);
    }

    [Fact]
    public void SeriesBudget_WithCollapsedCombinations_StaysBelowTheSdkDefaultLimit()
    {
        // 9 outcome labels x 2 token kinds x 3 modes collapsed combinations on top of the budget; the OpenTelemetry
        // SDK default cardinality limit per metric is 2000.
        Assert.True(TenantAuthorizationMetrics.MaxSeries + 9 * 2 * 3 < 2000);
    }

    [Fact]
    public void OutcomeLabel_UnknownOutcome_IsOther_NotADenial()
    {
        Assert.Equal(TenantAuthorizationMetrics.OtherOutcome,
            TenantAuthorizationMetrics.OutcomeLabel((TenantAuthorizationOutcome)999));
        Assert.False(TenantAuthorizationMetrics.IsRefusal((TenantAuthorizationOutcome)999));
    }

    [Fact]
    public void Request_WithoutRouteTenant_IsNotCounted()
    {
        var context = CreateContext(UserToken(Tenant), routeTenant: null);

        Run(context, new TenantAuthorizationOptions());

        Assert.Empty(_listener.Measurements);
    }

    [Fact]
    public async Task Host_WithoutTheRegistration_RunsWithoutTheCounter_AndWarnsOnce()
    {
        var context = CreateContextWithoutMetrics();
        var logger = new RecordingLogger<TenantAuthorizationMiddleware>();
        var middleware = new TenantAuthorizationMiddleware(_ => Task.CompletedTask,
            Options.Create(new TenantAuthorizationOptions()), logger);

        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(CreateContextWithoutMetrics());

        Assert.Empty(_listener.Measurements);
        Assert.Contains(TenantAuthorizationMetrics.CounterName, Assert.Single(logger.Warnings));
    }

    private HttpContext CreateContextWithoutMetrics()
    {
        var context = CreateContext(UserToken(Tenant), Tenant);
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    [Fact]
    public void AddOctoTenantAuthorization_RegistersTheMetricsAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddOctoTenantAuthorization(_ => { });
        services.AddOctoTenantAuthorization(_ => { });

        using var provider = services.BuildServiceProvider();
        Assert.Single(services, d => d.ServiceType == typeof(TenantAuthorizationMetrics));
        Assert.Same(provider.GetRequiredService<TenantAuthorizationMetrics>(),
            provider.GetRequiredService<TenantAuthorizationMetrics>());
    }

    [Fact]
    public void CounterName_IsPinned_OnTheTenantAuthorizationMeter()
    {
        Assert.Equal("octo.tenant_auth.decisions", TenantAuthorizationMetrics.CounterName);
        Assert.Equal("Meshmakers.Octo.TenantAuthorization", _metrics.Meter.Name);
    }

    // ---------------------------------------------------------------------------------------------

    private Dictionary<string, string?> SingleDecision()
    {
        var (value, tags) = Assert.Single(_listener.Measurements);
        Assert.Equal(1, value);
        return tags;
    }

    private (bool NextCalled, HttpContext Context) Invoke(IEnumerable<Claim> claims,
        TenantAuthorizationOptions? options = null, string routeTenant = Tenant, bool parentEndpoint = false,
        ITenantHierarchyReader? hierarchy = null)
    {
        var context = CreateContext(claims, routeTenant, parentEndpoint: parentEndpoint);
        context.RequestServices = new Services(_metrics, hierarchy);
        return Run(context, options ?? new TenantAuthorizationOptions());
    }

    private HttpContext CreateContext(IEnumerable<Claim> claims, string? routeTenant, bool parentEndpoint = false,
        bool routeEndpoint = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer dummy-token";
        if (routeTenant != null)
        {
            context.Request.RouteValues = new RouteValueDictionary
            {
                { InfrastructureCommon.TenantIdRoute, routeTenant }
            };
        }

        var metadata = parentEndpoint
            ? new EndpointMetadataCollection(new AllowParentTenantAdministrationAttribute())
            : EndpointMetadataCollection.Empty;
        context.SetEndpoint(routeEndpoint
            ? new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(RouteTemplate), 0, metadata,
                "backup (test)")
            : new Endpoint(_ => Task.CompletedTask, metadata, "no route (test)"));
        context.RequestServices = new Services(_metrics, null);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return context;
    }

    private static (bool NextCalled, HttpContext Context) Run(HttpContext context, TenantAuthorizationOptions options)
    {
        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }, Options.Create(options), NullLogger<TenantAuthorizationMiddleware>.Instance);
        middleware.InvokeAsync(context).GetAwaiter().GetResult();
        return (nextCalled, context);
    }

    private static IEnumerable<Claim> UserToken(string? tenant)
    {
        yield return new Claim("sub", Subject);
        yield return new Claim("client_id", "studio");
        if (tenant != null)
        {
            yield return new Claim("tenant_id", tenant);
        }
    }

    private static IEnumerable<Claim> ServiceToken(string clientId, string? tenant)
    {
        yield return new Claim("client_id", clientId);
        if (tenant != null)
        {
            yield return new Claim("tenant_id", tenant);
        }
    }

    private sealed class Services(TenantAuthorizationMetrics metrics, ITenantHierarchyReader? hierarchy)
        : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(TenantAuthorizationMetrics))
            {
                return metrics;
            }

            return serviceType == typeof(ITenantHierarchyReader) ? hierarchy : null;
        }
    }

    /// <summary>Collects the measurements of <c>octo.tenant_auth.decisions</c> of one metrics instance.</summary>
    private sealed class DecisionListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public DecisionListener(TenantAuthorizationMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter) &&
                    instrument.Name == TenantAuthorizationMetrics.CounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value?.ToString();
                }

                lock (Measurements)
                {
                    Measurements.Add((value, copy));
                }
            });
            _listener.Start();
        }

        public List<(long Value, Dictionary<string, string?> Tags)> Measurements { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
