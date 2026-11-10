using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RestDeprecation.Tests;

/// <summary>
///     AB#8415: with <c>Wave</c> the dates come from <c>RestDeprecation:Waves:&lt;wave&gt;</c>; a date that is not configured
///     leaves its header out, while Link, OpenAPI flag and counter always apply.
/// </summary>
public sealed class WaveTests : IAsyncLifetime
{
    private readonly MeasurementCollector _measurements = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.StartAsync(TestWave.Configure(TestWave.Configuration), controllers: typeof(WaveTestController));
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _measurements.Dispose();
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task DatedWave_CarriesConfiguredDeprecationAndSunset()
    {
        var response = await PutAsync("dated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestDeprecation.DeprecationHeader, Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.Equal(TestDeprecation.SunsetHeader, Assert.Single(response.Headers.GetValues("Sunset")));
        Assert.Equal(TestDeprecation.LinkHeader, Assert.Single(response.Headers.GetValues("Link")));
    }

    [Fact]
    public async Task SinceOnlyWave_CarriesDeprecationWithoutSunset()
    {
        var response = await PutAsync("since-only");

        Assert.Equal(TestDeprecation.DeprecationHeader, Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.Equal(TestDeprecation.LinkHeader, Assert.Single(response.Headers.GetValues("Link")));
    }

    [Fact]
    public async Task UndatedWave_CarriesLinksOnly_AndIsCounted()
    {
        var response = await PutAsync("undated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Deprecation"));
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.Equal([TestDeprecation.LinkHeader, TestDeprecation.SuccessorLinkHeader],
            response.Headers.GetValues("Link").ToArray());
        Assert.Contains(_measurements.Snapshot(), m => m.Endpoint == "PUT /{tenantId}/v1/wave/{userName}/undated" &&
                                                       m.ClientId == TestCaller.ClientId && m.StatusClass == "2xx");
    }

    [Fact]
    public async Task OpenApi_DatedWaveShowsSunset_UndatedWaveShowsDeprecatedNotice()
    {
        await using var app = await TestApp.StartAsync(
            builder =>
            {
                TestWave.Configure(TestWave.Configuration)(builder);
                builder.Services
                    .AddOctoApiVersioningAndDocumentation(o => o.AuthorityUrl = "https://identity.example.test/")
                    .AddVersion();
            },
            a => a.MapOpenApi(),
            typeof(WaveTestController));
        using var client = app.GetTestClient();
        var json = await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var paths = JsonNode.Parse(json)!["paths"]!.AsObject();

        var dated = paths["/{tenantId}/v1/wave/{userName}/dated"]!["put"]!;
        Assert.True(dated["deprecated"]!.GetValue<bool>());
        Assert.Equal($"Replaces the roles of a user.\n\nSunset {TestDeprecation.Sunset}, use {TestDeprecation.Link}.",
            dated["description"]!.GetValue<string>());

        var undated = paths["/{tenantId}/v1/wave/{userName}/undated"]!["put"]!;
        Assert.True(undated["deprecated"]!.GetValue<bool>());
        Assert.Equal($"Deprecated, use {TestDeprecation.Link}. Successor version: {TestDeprecation.Successor}.",
            undated["description"]!.GetValue<string>());
    }

    private async Task<HttpResponseMessage> PutAsync(string operation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/{TestCaller.TenantId}/v1/wave/{Uri.EscapeDataString(TestCaller.UserName)}/{operation}");
        request.Headers.Add(TestCaller.AuthenticateHeader, "1");
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}

/// <summary>AB#8415: an invalid wave attribute or wave configuration fails the service at startup.</summary>
public class WaveStartupValidationTests
{
    [Theory]
    [InlineData(typeof(WaveWithDatesController), "WaveWithDatesController.WaveWithDates",
        "Since and Sunset must not be set together with Wave 'Dated'")]
    [InlineData(typeof(BadWaveNameController), "BadWaveNameController.BadWaveName",
        "Wave 'identity-users-1' must consist of letters and digits")]
    [InlineData(typeof(WaveWithoutLinkController), "WaveWithoutLinkController.WaveWithoutLink",
        "Link '' is not an absolute http(s) URL")]
    public async Task InvalidWaveAttribute_FailsStartup_NamingTheAction(Type controller, string action, string reason)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await TestApp.StartAsync(controllers: controller);
        });

        Assert.Contains(action, exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Theory]
    [InlineData("2027-03-01", "2027-03-01", "RestDeprecation:Waves:Configured:Sunset 2027-03-01 must be after RestDeprecation:Waves:Configured:Since 2027-03-01")]
    [InlineData("01.12.2026", null, "RestDeprecation:Waves:Configured:Since '01.12.2026' is not a date in the format yyyy-MM-dd")]
    [InlineData(null, "2027-3-1", "RestDeprecation:Waves:Configured:Sunset '2027-3-1' is not a date in the format yyyy-MM-dd")]
    public async Task InvalidWaveConfiguration_FailsStartup_NamingTheActionAndKey(string? since, string? sunset,
        string reason)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RestDeprecation:Waves:Configured:Since"] = since,
            ["RestDeprecation:Waves:Configured:Sunset"] = sunset
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await TestApp.StartAsync(TestWave.Configure(configuration),
                controllers: typeof(ConfiguredWaveController));
        });

        Assert.Contains("ConfiguredWaveController.Operation", exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task WaveWithoutConfiguration_StartsNormally()
    {
        await using var app = await TestApp.StartAsync(controllers: typeof(ConfiguredWaveController));
    }
}
