using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     AB#6054, deny by default: an agent token (scope <c>octo_agent</c>) gets <c>403 AgentTokenNotAccepted</c> from
///     <c>UseOctoTenantAuthorization()</c> on every endpoint that is not marked <see cref="IAllowAgentTokens" /> — tenant
///     routes (GraphQL query and mutation alike, REST reads), routes without a tenant, anonymous endpoints and SignalR
///     routes that read the token from <c>?access_token=</c>. A token without the agent scope is not affected.
/// </summary>
public class AgentTokenRefusalPipelineTests
{
    private const string Tenant = "meshtest";

    /// <summary>
    ///     A host like asset-repo: the bearer scheme is the default, so <c>UseAuthentication()</c> puts the token's
    ///     principal on <c>HttpContext.User</c>.
    /// </summary>
    private static Task<IHost> StartBearerDefaultHostAsync() => StartAsync(defaultScheme: TestBearerHandler.SchemeName);

    /// <summary>
    ///     A host like identity or bot: the cookie is the default scheme, so <c>HttpContext.User</c> stays anonymous for an
    ///     endpoint whose policy does not name the bearer scheme; the refusal has to authenticate the bearer itself.
    /// </summary>
    private static Task<IHost> StartCookieDefaultHostAsync() => StartAsync(defaultScheme: TestCookieHandler.SchemeName);

    private static async Task<IHost> StartAsync(string defaultScheme)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddLogging();
                    s.AddAuthentication(defaultScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, _ => { })
                        .AddScheme<AuthenticationSchemeOptions, TestCookieHandler>(TestCookieHandler.SchemeName, _ => { });
                    s.AddAuthorization();
                    s.AddOctoTenantAuthorization(_ => { });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseOctoTenantAuthorization();
                    app.UseEndpoints(e =>
                    {
                        e.MapPost("/tenants/{tenantId}/graphQl", () => Results.Ok(new { data = "tenant data" }));
                        e.MapGet("/{tenantId}/v1/entities", () => Results.Ok(new { data = "tenant data" }));
                        e.MapGet("/system/v1/diagnostics", () => Results.Ok("system"));
                        e.MapGet("/operatorHub/negotiate", () => Results.Ok("hub"));
                        e.MapGet("/{tenantId}/_configuration", () => Results.Ok("public")).WithMetadata(new AllowAnonymousAttribute());
                        e.MapPost("/{tenantId}/mcp", () => Results.Ok("mcp")).AllowAgentTokens();
                    });
                });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AgentTokens.NotAcceptedErrorCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(403, body.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(AgentTokens.NotAcceptedMessage, body.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("tenant data", body.RootElement.GetRawText());
    }

    public static TheoryData<string, string> TenantDataRoutes => new()
    {
        { "POST", $"/tenants/{Tenant}/graphQl" }, // GraphQL query and mutation share the route
        { "GET", $"/{Tenant}/v1/entities" }, // REST read
        { "GET", "/system/v1/diagnostics" }, // a route without a tenant
        { "GET", "/operatorHub/negotiate" }, // a hub route without a tenant
        { "GET", $"/{Tenant}/_configuration" } // an anonymous endpoint
    };

    [Theory]
    [MemberData(nameof(TenantDataRoutes))]
    public async Task AgentToken_IsRefusedEverywhereButTheMcpAgentEndpoint(string method, string path)
    {
        using var host = await StartBearerDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(new HttpMethod(method), path, TestBearerHandler.AgentToken),
            TestContext.Current.CancellationToken);

        await AssertRefusedAsync(response);
    }

    [Theory]
    [MemberData(nameof(TenantDataRoutes))]
    public async Task AgentTokenThatAlsoCarriesOctoApi_IsRefused(string method, string path)
    {
        using var host = await StartBearerDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(
            Request(new HttpMethod(method), path, TestBearerHandler.AgentAndApiSpaceDelimitedToken),
            TestContext.Current.CancellationToken);

        await AssertRefusedAsync(response);
    }

    [Theory]
    [MemberData(nameof(TenantDataRoutes))]
    public async Task PlatformToken_IsNotAffected(string method, string path)
    {
        using var host = await StartBearerDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(new HttpMethod(method), path, TestBearerHandler.ApiToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AgentToken_IsAcceptedByTheEndpointMarkedAllowAgentTokens()
    {
        using var host = await StartBearerDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/{Tenant}/mcp", TestBearerHandler.AgentToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithoutToken_IsNotAffected()
    {
        using var host = await StartBearerDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/{Tenant}/_configuration", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(TenantDataRoutes))]
    public async Task AgentToken_IsRefused_WhenTheCookieIsTheDefaultScheme(string method, string path)
    {
        using var host = await StartCookieDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(new HttpMethod(method), path, TestBearerHandler.AgentToken),
            TestContext.Current.CancellationToken);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task AgentTokenInTheAccessTokenQuery_IsRefused_OnAHubRoute()
    {
        // SignalR's WebSocket and SSE transports send the token as ?access_token=, not as a header.
        using var host = await StartCookieDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(
            Request(HttpMethod.Get, $"/operatorHub/negotiate?access_token={TestBearerHandler.AgentToken}", null),
            TestContext.Current.CancellationToken);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task PlatformToken_IsNotAffected_WhenTheCookieIsTheDefaultScheme()
    {
        using var host = await StartCookieDefaultHostAsync();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Request(HttpMethod.Get, "/system/v1/diagnostics", TestBearerHandler.ApiToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("octo_agent", true)]
    [InlineData("openid octo_agent", true)]
    [InlineData("octo_api", false)]
    [InlineData("octo_agents", false)]
    [InlineData("OCTO_AGENT", false)]
    public void IsAgentToken_ReadsBothScopeEncodings_AndComparesOrdinally(string scope, bool expected)
    {
        var oneClaim = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", scope)], "Bearer"));
        var claimPerScope = new ClaimsPrincipal(new ClaimsIdentity(
            scope.Split(' ').Select(s => new Claim("scope", s)), "Bearer"));

        Assert.Equal(expected, AgentTokens.IsAgentToken(oneClaim));
        Assert.Equal(expected, AgentTokens.IsAgentToken(claimPerScope));
        Assert.False(AgentTokens.IsAgentToken(null));
    }

    /// <summary>
    ///     Stands in for the JWT bearer handler: the "token" names the scopes of the principal it produces. It reads the
    ///     header and, like the services' <c>OnMessageReceived</c> for SignalR, the <c>access_token</c> query.
    /// </summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Bearer";
        public const string AgentToken = "agent";
        public const string AgentAndApiSpaceDelimitedToken = "agent-and-api";
        public const string ApiToken = "api";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..]
                : Request.Query["access_token"].ToString();
            Claim[]? scopes = token switch
            {
                AgentToken => [new Claim("scope", "octo_agent")],
                AgentAndApiSpaceDelimitedToken => [new Claim("scope", "octo_agent octo_api")],
                ApiToken => [new Claim("scope", "octo_api"), new Claim("scope", "openid")],
                _ => null
            };
            if (scopes == null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                scopes.Concat([new Claim("client_id", "test-client"), new Claim("tenant_id", Tenant)]), "Bearer");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    /// <summary>A cookie scheme with no cookie: the default principal stays anonymous.</summary>
    private sealed class TestCookieHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestCookies";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
