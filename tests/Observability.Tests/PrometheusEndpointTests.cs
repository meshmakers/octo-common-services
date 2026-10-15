using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

using Meshmakers.Octo.Services.Observability;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     AB#5868: <c>/metrics</c> must never be served on the listener the public ingress forwards to.
/// </summary>
/// <remarks>
///     These tests run a real Kestrel host with two loopback listeners — the "public" one (what the
///     ingress reaches) and the "metrics" one — because the guard is the socket's local port and
///     TestServer has none.
/// </remarks>
public class PrometheusEndpointTests
{
    [Fact]
    public async Task Metrics_WithoutPrometheusPort_IsNotServedOnAnyListener()
    {
        await using var host = await StartAsync(prometheusPort: null);

        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(host.PublicUrl, "/metrics"));
        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(host.InternalUrl, "/metrics"));
    }

    [Fact]
    public async Task Metrics_WithPrometheusPort_IsServedOnlyOnThatPort()
    {
        await using var host = await StartAsync(prometheusPort: true);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(host.InternalUrl, "/metrics"));
        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync(host.PublicUrl, "/metrics"));
    }

    [Fact]
    public async Task Metrics_OnPublicPort_CannotBeReachedByForgingTheHostHeader()
    {
        await using var host = await StartAsync(prometheusPort: true);

        // The ingress forwards the caller's Host header. Naming the metrics port in it must not matter.
        Assert.Equal(HttpStatusCode.NotFound,
            await GetStatusAsync(host.PublicUrl, "/metrics", hostHeader: $"localhost:{host.InternalPort}"));
    }

    [Fact]
    public async Task HealthEndpoints_StayOnThePublicPort()
    {
        await using var host = await StartAsync(prometheusPort: true);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(host.PublicUrl, "/healthz/live"));
    }

    private static async Task<HttpStatusCode> GetStatusAsync(string baseUrl, string path, string? hostHeader = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (hostHeader != null)
        {
            request.Headers.Host = hostHeader;
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<TestHost> StartAsync(bool? prometheusPort)
    {
        var publicPort = FreePort();
        var internalPort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{publicPort}", $"http://127.0.0.1:{internalPort}");
        builder.Logging.ClearProviders();
        if (prometheusPort == true)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WebApplicationExtensions.PrometheusPortKey] = internalPort.ToString()
            });
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
