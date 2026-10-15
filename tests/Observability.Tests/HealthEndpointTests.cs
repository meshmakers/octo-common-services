using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Meshmakers.Octo.Services.Observability;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     AB#6739: the detailed health report (<c>/health</c>) needs a valid Bearer token; the kubelet probes
///     (<c>/healthz/*</c>) stay anonymous.
/// </summary>
/// <remarks>
///     Real Kestrel host with two loopback listeners, like <see cref="PrometheusEndpointTests" />. The
///     <c>Bearer</c> scheme is a stub that accepts exactly <see cref="ValidToken" />; the production schemes
///     (JwtBearer) are registered under the same name in every service.
/// </remarks>
public class HealthEndpointTests
{
    private const string ValidToken = "valid-token";

    [Fact]
    public async Task Health_Anonymous_Returns401AndNoReport()
    {
        await using var host = await StartAsync(withBearerScheme: true);

        foreach (var url in new[] { host.PublicUrl, host.InternalUrl })
        {
            var (status, body, challenge) = await GetAsync(url, "/health");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal(string.Empty, body);
            Assert.Equal("Bearer", challenge);
        }
    }

    [Fact]
    public async Task Health_WithValidToken_ReturnsTheReport()
    {
        await using var host = await StartAsync(withBearerScheme: true);

        var (status, body, _) = await GetAsync(host.PublicUrl, "/health", token: ValidToken);

        // The startup check is not complete in this bare host, so the report says Unhealthy (503). What matters:
        // the caller is let in and receives the JSON report, not a 401.
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"answered {status}");
        Assert.Contains("\"status\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid-token")]
    [InlineData("")]
    public async Task Health_WithInvalidToken_Returns401(string token)
    {
        await using var host = await StartAsync(withBearerScheme: true);

        var (status, body, _) = await GetAsync(host.PublicUrl, "/health", token: token);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("/Health")]
    [InlineData("/health/")]
    public async Task Health_OtherSpellingsOfThePath_AreGuardedToo(string path)
    {
        await using var host = await StartAsync(withBearerScheme: true);

        var (status, body, _) = await GetAsync(host.PublicUrl, path);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"entries\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_ForgedHostHeader_DoesNotChangeTheDecision()
    {
        await using var host = await StartAsync(withBearerScheme: true);

        var (status, _, _) = await GetAsync(host.PublicUrl, "/health", hostHeader: $"localhost:{host.InternalPort}");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Health_OnAHostWithoutBearerScheme_FailsClosed()
    {
        await using var host = await StartAsync(withBearerScheme: false);

        var (status, body, _) = await GetAsync(host.PublicUrl, "/health", token: ValidToken);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    public async Task HealthProbes_StayAnonymous(string path)
    {
        await using var host = await StartAsync(withBearerScheme: true);

        var (status, body, _) = await GetAsync(host.PublicUrl, path);

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"{path} answered {status}");
        Assert.DoesNotContain("entries", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthLive_Anonymous_Returns200()
    {
        await using var host = await StartAsync(withBearerScheme: true);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(host.PublicUrl, "/healthz/live")).Status);
    }

    private static async Task<(HttpStatusCode Status, string Body, string? Challenge)> GetAsync(
        string baseUrl, string path, string? token = null, string? hostHeader = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (hostHeader != null)
        {
            request.Headers.Host = hostHeader;
        }

        using var response = await client.SendAsync(request);
        var challenge = response.Headers.WwwAuthenticate.Count > 0 ? response.Headers.WwwAuthenticate.ToString() : null;
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), challenge);
    }

    private static async Task<TestHost> StartAsync(bool withBearerScheme)
    {
        var publicPort = FreePort();
        var internalPort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{publicPort}", $"http://127.0.0.1:{internalPort}");
        builder.Logging.ClearProviders();
        if (withBearerScheme)
        {
            builder.Services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, StubBearerHandler>("Bearer", _ => { });
        }

        builder.AddObservability();
        var app = builder.Build();
        app.MapObservability();
        await app.StartAsync();

        return new TestHost(app, $"http://127.0.0.1:{publicPort}", $"http://127.0.0.1:{internalPort}", internalPort);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class StubBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.ToString() != $"Bearer {ValidToken}")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("sub", "tester")], "Bearer");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Bearer")));
        }
    }

    private sealed class TestHost(WebApplication app, string publicUrl, string internalUrl, int internalPort)
        : IAsyncDisposable
    {
        public string PublicUrl { get; } = publicUrl;
        public string InternalUrl { get; } = internalUrl;
        public int InternalPort { get; } = internalPort;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
