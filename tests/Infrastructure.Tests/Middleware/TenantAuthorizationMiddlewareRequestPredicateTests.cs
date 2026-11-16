using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     The per-request predicate <see cref="IAllowParentTenantAdministrationRequest" /> of an endpoint that also serves
///     tenant content (a GraphQL endpoint): a user token of a parent tenant passes only when the predicate classifies the
///     request as exclusively administration <b>and</b> the route tenant is a registered child. Everything else on the
///     endpoint keeps the exact match; service tokens and own-tenant tokens never reach the predicate.
/// </summary>
public sealed class TenantAuthorizationMiddlewareRequestPredicateTests : IDisposable
{
    private const string Parent = "parent1";
    private const string Child = "child1";
    private const string Foreign = "unrelated1";
    private const string RouteTemplate = "tenants/{tenantId}/graphQl";

    private readonly TenantAuthorizationMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<string?> _outcomes = [];

    public TenantAuthorizationMiddlewareRequestPredicateTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.Meter) &&
                instrument.Name == TenantAuthorizationMetrics.CounterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == TenantAuthorizationMetrics.OutcomeTag)
                {
                    lock (_outcomes)
                    {
                        _outcomes.Add(tag.Value?.ToString());
                    }
                }
            }
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    private sealed class Predicate(bool answer, bool throws = false) : IAllowParentTenantAdministrationRequest
    {
        public int Calls { get; private set; }

        public ValueTask<bool> IsParentTenantAdministrationRequestAsync(HttpContext context)
        {
            Calls++;
            if (throws)
            {
                throw new InvalidOperationException("broken predicate");
            }

            return ValueTask.FromResult(answer);
        }
    }

    private sealed class Services(TenantAuthorizationMetrics metrics, ITenantHierarchyReader hierarchy) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(TenantAuthorizationMetrics) ? metrics :
            serviceType == typeof(ITenantHierarchyReader) ? hierarchy : null;
    }

    private static ITenantHierarchyReader Hierarchy()
    {
        var hierarchy = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => hierarchy.IsChildTenantAsync(A<string>._, A<string>._)).Returns(false);
        A.CallTo(() => hierarchy.IsChildTenantAsync(Parent, Child)).Returns(true);
        return hierarchy;
    }

    private static IEnumerable<Claim> User(string tenant) =>
    [
        new("sub", "6600000000000000000000ff"), new("client_id", "studio"), new("tenant_id", tenant)
    ];

    private static IEnumerable<Claim> Service(string tenant) => [new("client_id", "parent-client"), new("tenant_id", tenant)];

    private async Task<(bool NextCalled, HttpContext Context)> InvokeAsync(IEnumerable<Claim> claims, string routeTenant,
        object? metadata, ITenantHierarchyReader? hierarchy = null,
        UserTokenTenantEnforcementMode mode = UserTokenTenantEnforcementMode.Enforce)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer dummy-token";
        context.Request.RouteValues = new RouteValueDictionary { { InfrastructureCommon.TenantIdRoute, routeTenant } };
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(RouteTemplate), 0,
            metadata == null ? EndpointMetadataCollection.Empty : new EndpointMetadataCollection(metadata), "graphql (test)"));
        context.RequestServices = new Services(_metrics, hierarchy ?? Hierarchy());
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var nextCalled = false;
        var middleware = new TenantAuthorizationMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }, Options.Create(new TenantAuthorizationOptions
            {
                UserTokenEnforcement = mode,
                ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.Enforce
            }), NullLogger<TenantAuthorizationMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        return (nextCalled, context);
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public async Task AdministrationRequestOfTheParent_OnItsChild_IsAllowed_AndCountedAsAllowedParentAdmin()
    {
        var predicate = new Predicate(true);

        var (nextCalled, _) = await InvokeAsync(User(Parent), Child, predicate);

        Assert.True(nextCalled);
        Assert.Equal(1, predicate.Calls);
        Assert.Equal(["allowed_parent_admin"], _outcomes);
    }

    [Fact]
    public async Task AnyOtherRequestOfTheParent_KeepsTheExactMatch()
    {
        var (nextCalled, context) = await InvokeAsync(User(Parent), Child, new Predicate(false));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("Sign in to tenant", Body(context));
        Assert.Equal(["denied"], _outcomes);
    }

    [Fact]
    public async Task AdministrationRequest_OnATenantThatIsNotAChild_IsDenied_WithTheCombinedMessage()
    {
        var (nextCalled, context) = await InvokeAsync(User(Parent), Foreign, new Predicate(true));

        Assert.False(nextCalled);
        Assert.Contains("is not an existing child tenant", Body(context));
        Assert.Equal(["denied"], _outcomes);
    }

    [Fact]
    public async Task AThrowingPredicate_FailsClosed()
    {
        var (nextCalled, _) = await InvokeAsync(User(Parent), Child, new Predicate(true, throws: true));

        Assert.False(nextCalled);
        Assert.Equal(["denied"], _outcomes);
    }

    [Fact]
    public async Task InLogOnly_AGrantedAdministrationRequest_IsNotReportedAsWouldDeny()
    {
        var (nextCalled, _) = await InvokeAsync(User(Parent), Child, new Predicate(true),
            mode: UserTokenTenantEnforcementMode.LogOnly);

        Assert.True(nextCalled);
        Assert.Equal(["allowed_parent_admin"], _outcomes);
    }

    [Fact]
    public async Task OwnTenantTokens_NeverAskThePredicate()
    {
        var predicate = new Predicate(true);

        var (nextCalled, _) = await InvokeAsync(User(Child), Child, predicate);

        Assert.True(nextCalled);
        Assert.Equal(0, predicate.Calls);
    }

    [Fact]
    public async Task ServiceTokens_NeverUseThePredicate()
    {
        var predicate = new Predicate(true);

        var (nextCalled, context) = await InvokeAsync(Service(Parent), Child, predicate);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, predicate.Calls);
    }

    [Fact]
    public async Task WithoutHierarchyAnswer_TheChildCheckIsStillRequired()
    {
        var hierarchy = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => hierarchy.IsChildTenantAsync(A<string>._, A<string>._)).Returns(false);

        var (nextCalled, _) = await InvokeAsync(User(Parent), Child, new Predicate(true), hierarchy);

        Assert.False(nextCalled);
    }

    [Fact]
    public void ThePredicateInterface_IsNoEndpointMarker() =>
        Assert.False(typeof(IAllowParentTenantAdministration).IsAssignableFrom(typeof(IAllowParentTenantAdministrationRequest)));
}
