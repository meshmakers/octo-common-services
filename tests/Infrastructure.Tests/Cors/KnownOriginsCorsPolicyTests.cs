using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Cors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Cors;

/// <summary>
///     AB#7270 (AB#7265 AC 4): application-level CORS regression tests of the shared known-origins policy
///     (<see cref="CorsPolicyProvider" />) through a real ASP.NET Core pipeline — <c>UseCors()</c> without a
///     policy name, as every OctoMesh service host calls it. Asserts that an unknown origin gets no
///     <c>Access-Control-Allow-Origin</c> on success and on error responses, that a registered client origin of
///     the addressed tenant gets it, and that no response ever carries <c>Access-Control-Allow-Credentials</c>
///     (the API routes are bearer-only).
/// </summary>
public sealed class KnownOriginsCorsPolicyTests : IAsyncLifetime
{
    private const string SystemTenantId = "octosystem";
    private const string Tenant = "meshtest";
    private const string KnownOrigin = "https://studio.example.test";
    private const string TenantOnlyOrigin = "https://app.meshtest.example.test";
    private const string UnknownOrigin = "https://evil.example.com";

    private const string AllowOrigin = "Access-Control-Allow-Origin";
    private const string AllowCredentials = "Access-Control-Allow-Credentials";
    private const string ExposeHeaders = "Access-Control-Expose-Headers";
    private const string MaxAge = "Access-Control-Max-Age";

    private IHost? _host;
    private HttpClient? _client;

    private HttpClient Client => _client ?? throw new InvalidOperationException("Host not started");

    public async ValueTask InitializeAsync()
    {
        _host = await StartHostAsync(options =>
        {
            options.ExposedHeaders.Add("Upload-Offset");
            options.ExposedHeaders.Add("Tus-Resumable");
            options.PreflightMaxAge = TimeSpan.FromHours(1);
        });
        _client = _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host != null)
        {
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }

    private static async Task<IHost> StartHostAsync(Action<OctoCorsOptions>? configureCors = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddCors();
                    services.AddOptions<OctoCorsOptions>();
                    if (configureCors != null)
                    {
                        services.Configure(configureCors);
                    }

                    services.AddSingleton(Options.Create(new OctoSystemConfiguration { SystemTenantId = SystemTenantId }));
                    services.AddScoped<IKnownOriginsProvider, FakeKnownOriginsProvider>();
                    services.AddSingleton<ICorsPolicyProvider, CorsPolicyProvider>();
                    services.AddAuthentication(TestBearerHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
                    services.AddAuthorization();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseCors();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/system/v1/ok", () => Results.Ok("ok"));
                        endpoints.MapGet("/system/v1/fail", () => Results.Problem(statusCode: StatusCodes.Status500InternalServerError));
                        endpoints.MapGet("/{tenantId}/v1/ok", (string tenantId) => Results.Ok(tenantId));
                        endpoints.MapGet("/system/v1/bearer", () => Results.Ok("secret")).RequireAuthorization();
                    });
                });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string origin, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", origin);
        if (method == HttpMethod.Options)
        {
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");
        }

        if (bearer != null)
        {
            request.Headers.Add("Authorization", $"Bearer {bearer}");
        }

        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Theory]
    [InlineData("/system/v1/ok", HttpStatusCode.OK)]
    [InlineData("/system/v1/fail", HttpStatusCode.InternalServerError)]
    [InlineData("/system/v1/bearer", HttpStatusCode.Unauthorized)]
    [InlineData("/system/v1/does-not-exist", HttpStatusCode.NotFound)]
    public async Task UnknownOrigin_GetsNoCorsHeaders_OnSuccessAndErrorResponses(string path, HttpStatusCode expected)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, path, UnknownOrigin), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(Header(response, AllowOrigin));
        Assert.Null(Header(response, AllowCredentials));
    }

    [Fact]
    public async Task UnknownOrigin_Preflight_GetsNoCorsHeaders()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Options, "/system/v1/ok", UnknownOrigin),
            TestContext.Current.CancellationToken);

        Assert.Null(Header(response, AllowOrigin));
        Assert.Null(Header(response, AllowCredentials));
    }

    [Theory]
    [InlineData("/system/v1/ok", HttpStatusCode.OK)]
    [InlineData("/system/v1/fail", HttpStatusCode.InternalServerError)]
    [InlineData("/system/v1/bearer", HttpStatusCode.Unauthorized)]
    public async Task KnownOrigin_GetsAllowOrigin_AndExposedHeaders(string path, HttpStatusCode expected)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, path, KnownOrigin), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(KnownOrigin, Header(response, AllowOrigin));
        Assert.Contains("Upload-Offset", Header(response, ExposeHeaders) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tus-Resumable", Header(response, ExposeHeaders) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Header(response, AllowCredentials));
    }

    [Fact]
    public async Task KnownOrigin_Preflight_AllowsOriginAndAuthorizationHeader_WithoutCredentials()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Options, "/system/v1/bearer", KnownOrigin),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(KnownOrigin, Header(response, AllowOrigin));
        Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("3600", Header(response, MaxAge));
        Assert.Null(Header(response, AllowCredentials));
    }

    [Fact]
    public async Task BearerOnlyRoute_WithValidBearer_KnownOrigin_HasNoAllowCredentials()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, "/system/v1/bearer", KnownOrigin, TestBearerHandler.ValidToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(KnownOrigin, Header(response, AllowOrigin));
        Assert.Null(Header(response, AllowCredentials));
    }

    [Fact]
    public async Task TenantRoute_UsesTheOriginsOfTheAddressedTenant()
    {
        var own = await Client.SendAsync(Request(HttpMethod.Get, $"/{Tenant}/v1/ok", TenantOnlyOrigin),
            TestContext.Current.CancellationToken);
        var system = await Client.SendAsync(Request(HttpMethod.Get, "/system/v1/ok", TenantOnlyOrigin),
            TestContext.Current.CancellationToken);

        Assert.Equal(TenantOnlyOrigin, Header(own, AllowOrigin));
        Assert.Null(Header(system, AllowOrigin));
    }

    [Fact]
    public async Task WithoutOctoCorsOptions_NoExposedHeadersAndNoMaxAge()
    {
        using var host = await StartHostAsync();
        using var client = host.GetTestClient();

        var get = await client.SendAsync(Request(HttpMethod.Get, "/system/v1/ok", KnownOrigin), TestContext.Current.CancellationToken);
        var preflight = await client.SendAsync(Request(HttpMethod.Options, "/system/v1/ok", KnownOrigin),
            TestContext.Current.CancellationToken);

        Assert.Equal(KnownOrigin, Header(get, AllowOrigin));
        Assert.Null(Header(get, ExposeHeaders));
        Assert.Null(Header(preflight, MaxAge));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Registered client origins per tenant; unknown tenants fall back to the system tenant like the real provider.</summary>
    private sealed class FakeKnownOriginsProvider : IKnownOriginsProvider
    {
        public Task<IReadOnlyCollection<string>> GetKnownOriginsAsync(string tenantId)
        {
            IReadOnlyCollection<string> origins = tenantId == Tenant
                ? [KnownOrigin, TenantOnlyOrigin]
                : [KnownOrigin];
            return Task.FromResult(origins);
        }
    }

    /// <summary>Accepts exactly one bearer token; stands in for the JWT bearer handler.</summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Bearer";
        public const string ValidToken = "valid-token";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (header != $"Bearer {ValidToken}")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("sub", "user")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
