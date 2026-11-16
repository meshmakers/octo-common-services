using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Infrastructure.Tests.TenantGeneration;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     A request the tenant gate lets through can reach an endpoint that resolves the route tenant and fails with
///     <see cref="TenantException" /> (<see cref="TenantException.IsTenantNotFound" />) because the tenant was deleted or
///     never existed. The gate answers it with its own error contract (404 for a caller of that tenant, 403 with the
///     denial message for any other caller) and never lets the exception, its message or a stack trace reach the response.
/// </summary>
public class TenantAuthorizationMiddlewareTenantNotFoundTests
{
    private const string RouteTenant = "deletedtenant";
    private const string ParentTenant = "parenttenant";
    private const string ForeignTenant = "othertenant";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    public TenantAuthorizationMiddlewareTenantNotFoundTests()
    {
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).Returns(false);
    }

    private HttpContext CreateContext(IEnumerable<Claim> claims, bool parentTenantAdministrationEndpoint = false,
        ITenantHierarchyReader? hierarchyReader = null, bool withSystemContext = true, bool withBearer = true,
        string authenticationType = "Bearer")
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TenantAuthorizationMetrics());
        if (withSystemContext)
        {
            services.AddSingleton(_systemContext);
        }

        if (hierarchyReader != null)
        {
            services.AddSingleton(hierarchyReader);
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        if (withBearer)
        {
            context.Request.Headers.Authorization = "Bearer dummy-token";
        }

        context.Request.RouteValues = new RouteValueDictionary { { InfrastructureCommon.TenantIdRoute, RouteTenant } };
        if (parentTenantAdministrationEndpoint)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new AllowParentTenantAdministrationAttribute()),
                "POST /{tenantId}/v1/tenants/backup (test endpoint)"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
        return context;
    }

    private static IEnumerable<Claim> UserToken(string tenantId) =>
    [
        new("sub", "6600000000000000000000ff"),
        new("tenant_id", tenantId),
        new("client_id", "octo-data-refinery-studio")
    ];

    private static IEnumerable<Claim> ServiceToken(string clientId, string tenantId) =>
    [
        new("client_id", clientId),
        new("tenant_id", tenantId)
    ];

    private readonly RecordingLogger<TenantAuthorizationMiddleware> _logger = new();

    private async Task InvokeAsync(HttpContext context, Exception thrown, TenantAuthorizationOptions? options = null)
    {
        var middleware = new TenantAuthorizationMiddleware(_ => throw thrown,
            Options.Create(options ?? new TenantAuthorizationOptions()), _logger);
        await middleware.InvokeAsync(context);
    }

    private static async Task<string> ReadMessageAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        AssertNoExceptionDetails(body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("message").GetString()!;
    }

    /// <summary>Neither the exception type nor a stack frame may ever reach the response body.</summary>
    internal static void AssertNoExceptionDetails(string body)
    {
        Assert.DoesNotContain(nameof(TenantException), body);
        Assert.DoesNotContain("   at ", body);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Own_tenant_user_token_on_a_deleted_tenant_gets_404_with_a_message()
    {
        var context = CreateContext(UserToken(RouteTenant));

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal($"Tenant '{RouteTenant}' does not exist.", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task Cross_tenant_user_token_in_LogOnly_gets_the_403_of_the_enforced_denial()
    {
        // LogOnly lets the request through; the unknown tenant must not answer differently from a refused one.
        var context = CreateContext(UserToken(ForeignTenant));

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant),
            new TenantAuthorizationOptions { UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        var message = await ReadMessageAsync(context);
        Assert.Contains($"Sign in to tenant '{RouteTenant}'", message);
        Assert.DoesNotContain("does not exist", message);
    }

    [Fact]
    public async Task Parent_administrator_on_a_child_deleted_within_the_hierarchy_cache_gets_the_combined_403()
    {
        // The cached hierarchy still reports the child, so the gate grants the request even in Enforce.
        var reader = A.Fake<ITenantHierarchyReader>();
        A.CallTo(() => reader.IsChildTenantAsync(ParentTenant, RouteTenant)).Returns(true);
        var context = CreateContext(UserToken(ParentTenant), parentTenantAdministrationEndpoint: true,
            hierarchyReader: reader);

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("is not an existing child tenant", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task Own_tenant_service_token_gets_404()
    {
        var context = CreateContext(ServiceToken("octo-pipeline-sa-1", RouteTenant));

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal($"Tenant '{RouteTenant}' does not exist.", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task Cross_tenant_service_token_in_LogOnly_gets_the_403_of_the_enforced_denial()
    {
        var context = CreateContext(ServiceToken("octo-pipeline-sa-1", ForeignTenant));

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant),
            new TenantAuthorizationOptions { ServiceTokenEnforcement = ServiceTokenTenantEnforcementMode.LogOnly });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains($"acr_values=tenant:{RouteTenant}", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task Allow_listed_cross_tenant_service_client_gets_404()
    {
        var context = CreateContext(ServiceToken("octo-platform-worker", ForeignTenant));
        var options = new TenantAuthorizationOptions { CrossTenantServiceClientIds = ["octo-platform-worker"] };

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant), options);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal($"Tenant '{RouteTenant}' does not exist.", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task A_missing_tenant_other_than_the_route_tenant_keeps_its_way()
    {
        // The route tenant exists; the endpoint resolved another tenant (e.g. a child) that does not.
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(RouteTenant)).Returns(true);
        var context = CreateContext(UserToken(RouteTenant));

        await Assert.ThrowsAsync<TenantException>(() =>
            InvokeAsync(context, TenantException.TenantDoesNotExist("somechild")));
    }

    [Fact]
    public async Task Without_a_system_context_the_exception_keeps_its_way()
    {
        var context = CreateContext(UserToken(RouteTenant), withSystemContext: false);

        await Assert.ThrowsAsync<TenantException>(() =>
            InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant)));
    }

    [Fact]
    public async Task An_unreadable_registry_keeps_the_original_exception()
    {
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._))
            .ThrowsAsync(new InvalidOperationException("registry down"));
        var context = CreateContext(UserToken(RouteTenant));
        var original = TenantException.TenantDoesNotExist(RouteTenant);

        var thrown = await Assert.ThrowsAsync<TenantException>(() => InvokeAsync(context, original));

        Assert.Same(original, thrown);
        Assert.Single(_logger.Warnings);
    }

    [Fact]
    public async Task Other_tenant_exceptions_are_not_mapped()
    {
        var context = CreateContext(UserToken(RouteTenant));

        await Assert.ThrowsAsync<TenantException>(() =>
            InvokeAsync(context, TenantException.TenantIdNotAvailable(RouteTenant)));
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).MustNotHaveHappened();
    }
    [Fact]
    public async Task Request_without_a_bearer_token_gets_404()
    {
        var context = CreateContext([], withBearer: false, authenticationType: "Identity.Application");

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal($"Tenant '{RouteTenant}' does not exist.", await ReadMessageAsync(context));
    }

    [Fact]
    public async Task Cookie_principal_behind_an_injected_bearer_header_gets_404()
    {
        var context = CreateContext(UserToken(ForeignTenant), authenticationType: "Identity.Application");

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        await ReadMessageAsync(context);
    }

    [Fact]
    public async Task Allow_listed_service_client_on_its_own_tenant_gets_404()
    {
        var context = CreateContext(ServiceToken("octo-platform-worker", RouteTenant));
        var options = new TenantAuthorizationOptions { CrossTenantServiceClientIds = ["octo-platform-worker"] };

        await InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant), options);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task A_response_that_has_already_started_is_not_touched()
    {
        var context = CreateContext(UserToken(RouteTenant));
        var responseFeature = new StartedResponseFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(responseFeature);

        await Assert.ThrowsAsync<TenantException>(() =>
            InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant)));
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task An_aborted_request_keeps_the_original_exception()
    {
        var context = CreateContext(UserToken(RouteTenant));
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        await Assert.ThrowsAsync<TenantException>(() =>
            InvokeAsync(context, TenantException.TenantDoesNotExist(RouteTenant)));
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task A_request_aborted_while_the_probe_fails_keeps_the_original_exception()
    {
        var context = CreateContext(UserToken(RouteTenant));
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._))
            .Invokes(() => aborted.Cancel())
            .ThrowsAsync(new OperationCanceledException(aborted.Token));
        var original = TenantException.TenantDoesNotExist(RouteTenant);

        var thrown = await Assert.ThrowsAsync<TenantException>(() => InvokeAsync(context, original));

        Assert.Same(original, thrown);
        Assert.Empty(_logger.Warnings);
    }

    [Fact]
    public async Task A_request_aborted_while_the_probe_succeeds_is_not_mapped()
    {
        var context = CreateContext(UserToken(RouteTenant));
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._))
            .Invokes(() => aborted.Cancel())
            .Returns(false);
        var original = TenantException.TenantDoesNotExist(RouteTenant);

        var thrown = await Assert.ThrowsAsync<TenantException>(() => InvokeAsync(context, original));

        Assert.Same(original, thrown);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
