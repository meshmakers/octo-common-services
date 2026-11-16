using System.Net;
using System.Security.Claims;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     The tenant gate in a real ASP.NET Core pipeline, behind the exception layers the hosts use
///     (<c>UseDeveloperExceptionPage()</c> on the local stack, none in production): an endpoint that resolves a route
///     tenant which does not exist answers 404/403 as JSON, never a 500 with the exception or its stack trace.
/// </summary>
public class TenantNotFoundPipelineTests
{
    private const string DeletedTenant = "deletedtenant";
    private const string ExistingTenant = "parenttenant";

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();

    public TenantNotFoundPipelineTests()
    {
        A.CallTo(() => _systemContext.IsTenantRegisteredAsync(A<string>._))
            .ReturnsLazily((string tenantId) => string.Equals(tenantId, ExistingTenant, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IHost> StartAsync(string environment, UserTokenTenantEnforcementMode mode)
    {
        var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddLogging();
                    s.AddSingleton(_systemContext);
                    s.AddOctoTenantAuthorization(o => o.UserTokenEnforcement = mode);
                });
                web.Configure(app =>
                {
                    if (environment == Environments.Development)
                    {
                        app.UseDeveloperExceptionPage();
                    }

                    app.UseRouting();
                    // Stands in for UseAuthentication(): the JWT handler labels the identity "Bearer".
                    app.Use(async (ctx, next) =>
                    {
                        var tenant = ctx.Request.Headers["X-Test-Token-Tenant"].ToString();
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("sub", "6600000000000000000000ff"),
                            new Claim("tenant_id", tenant),
                            new Claim("client_id", "octo-data-refinery-studio")
                        ], "Bearer"));
                        await next(ctx);
                    });
                    app.UseOctoTenantAuthorization();
                    app.UseEndpoints(e => e.MapPost("/tenants/{tenantId}/graphQl", async (HttpContext ctx) =>
                    {
                        // What the GraphQL user context builder does for every request.
                        await _systemContext.FindTenantContextAsync(ctx.GetRouteValue("tenantId")!.ToString()!);
                        throw TenantException.TenantDoesNotExist(ctx.GetRouteValue("tenantId")!.ToString()!);
                    }));
                });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<HttpResponseMessage> PostAsync(IHost host, string routeTenant, string tokenTenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/tenants/{routeTenant}/graphQl")
        {
            Content = new StringContent("{\"query\":\"{ __typename }\"}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", "Bearer dummy-token");
        request.Headers.Add("X-Test-Token-Tenant", tokenTenant);
        return await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> AssertJsonWithoutExceptionDetailsAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        TenantAuthorizationMiddlewareTenantNotFoundTests.AssertNoExceptionDetails(body);
        return body;
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Foreign_token_in_LogOnly_on_a_deleted_tenant_gets_403_not_a_stack_trace(string environment)
    {
        // LogOnly is how the asset repository runs the user path: the gate lets the request through.
        using var host = await StartAsync(environment, UserTokenTenantEnforcementMode.LogOnly);

        var response = await PostAsync(host, DeletedTenant, ExistingTenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains($"Sign in to tenant '{DeletedTenant}'", await AssertJsonWithoutExceptionDetailsAsync(response));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Own_token_of_a_deleted_tenant_gets_404_not_a_stack_trace(string environment)
    {
        using var host = await StartAsync(environment, UserTokenTenantEnforcementMode.Enforce);

        var response = await PostAsync(host, DeletedTenant, DeletedTenant);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("does not exist", await AssertJsonWithoutExceptionDetailsAsync(response));
    }

    [Fact]
    public async Task Unknown_tenant_with_a_foreign_token_in_Enforce_is_refused_by_the_gate_itself()
    {
        using var host = await StartAsync(Environments.Development, UserTokenTenantEnforcementMode.Enforce);

        var response = await PostAsync(host, "nevercreated", ExistingTenant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertJsonWithoutExceptionDetailsAsync(response);
        A.CallTo(() => _systemContext.FindTenantContextAsync(A<string>._)).MustNotHaveHappened();
    }
}
