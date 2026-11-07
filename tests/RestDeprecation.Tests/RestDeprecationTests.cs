using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace RestDeprecation.Tests;

/// <summary>
///     AB#8421: headers on every response of a marked action, the per-client counter and its label rules, nothing on
///     unmarked actions.
/// </summary>
public sealed class RestDeprecationTests : IAsyncLifetime
{
    private readonly MeasurementCollector _measurements = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.StartAsync(controllers: [typeof(UsersTestController), typeof(LegacyTestController)]);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _measurements.Dispose();
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task MarkedAction_200_CarriesDeprecationSunsetAndLink()
    {
        var response = await GetAsync($"/{TestCaller.TenantId}/v1/users/jane/password-state", authenticated: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertDeprecationHeaders(response);
    }

    [Fact]
    public async Task MarkedAction_400FromModelValidation_CarriesHeaders()
    {
        var response = await GetAsync($"/{TestCaller.TenantId}/v1/users/jane/password-state?detail=99");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertDeprecationHeaders(response);
    }

    [Fact]
    public async Task MarkedAction_403FromAuthorizationMiddleware_CarriesHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/{TestCaller.TenantId}/v1/users/jane/reset-password");
        request.Headers.Add(TestCaller.AuthenticateHeader, "1");
        request.Headers.Add(TestCaller.RoleHeader, "reader");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertDeprecationHeaders(response);
    }

    [Fact]
    public async Task MarkedAction_401FromAuthorizationMiddleware_CarriesHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/{TestCaller.TenantId}/v1/users/jane/reset-password");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertDeprecationHeaders(response);
    }

    [Fact]
    public async Task MarkedAction_500FromExceptionHandler_CarriesHeaders()
    {
        var response = await GetAsync($"/{TestCaller.TenantId}/v1/users/jane/explode");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertDeprecationHeaders(response);
    }

    [Fact]
    public async Task ControllerAttribute_AppliesToEveryAction_ActionAttributeWins()
    {
        var inherited = await GetAsync($"/{TestCaller.TenantId}/v1/legacy/inherited");
        var overridden = await GetAsync($"/{TestCaller.TenantId}/v1/legacy/overridden");

        Assert.Equal("@1767225600", Assert.Single(inherited.Headers.GetValues("Deprecation")));
        Assert.Equal("Mon, 01 Jun 2026 00:00:00 GMT", Assert.Single(inherited.Headers.GetValues("Sunset")));
        AssertDeprecationHeaders(overridden);
    }

    [Fact]
    public async Task MarkedActionWithSuccessor_CarriesDeprecationAndSuccessorVersionLinks()
    {
        var response = await GetAsync($"/{TestCaller.TenantId}/v1/users/jane/with-successor", authenticated: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestDeprecation.DeprecationHeader, Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.Equal([TestDeprecation.LinkHeader, TestDeprecation.SuccessorLinkHeader],
            response.Headers.GetValues("Link").ToArray());
    }

    [Theory]
    [InlineData("/v1/users/jane", "GET /{tenantId}/v1/users/{userName}", HttpStatusCode.OK)]
    [InlineData("/v1/users/jane/unmarked-explode", "GET /{tenantId}/v1/users/{userName}/unmarked-explode",
        HttpStatusCode.InternalServerError)]
    public async Task UnmarkedAction_HasNoHeaderAndNoMeasurement(string path, string endpoint,
        HttpStatusCode expectedStatus)
    {
        var response = await GetAsync("/" + TestCaller.TenantId + path, authenticated: true);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.False(response.Headers.Contains("Deprecation"));
        Assert.False(response.Headers.Contains("Sunset"));
        Assert.False(response.Headers.Contains("Link"));
        Assert.DoesNotContain(_measurements.Snapshot(), m => m.Endpoint == endpoint);
    }

    [Fact]
    public async Task Counter_LabelsAreRouteTemplateClientIdAndStatusClass()
    {
        await GetAsync($"/{TestCaller.TenantId}/v1/users/{Uri.EscapeDataString(TestCaller.UserName)}/password-state",
            authenticated: true);

        var measurement = Assert.Single(_measurements.Snapshot(),
            m => m.Endpoint == "GET /{tenantId}/v1/users/{userName}/password-state" && m.ClientId == TestCaller.ClientId);
        Assert.Equal(1, measurement.Value);
        Assert.Equal("2xx", measurement.StatusClass);
        Assert.Equal(["client_id", "endpoint", "status_class"], measurement.Tags.Keys.Order());
    }

    [Fact]
    public async Task Counter_PipelineServiceAccount_IsReportedWithoutTenant()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/{TestCaller.TenantId}/v1/users/jane/password-state");
        request.Headers.Add(TestCaller.AuthenticateHeader, "1");
        request.Headers.Add(TestCaller.ClientIdHeader, "octo-pipeline-sa-" + TestCaller.TenantId);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_measurements.Snapshot(), m => m.Endpoint == "GET /{tenantId}/v1/users/{userName}/password-state" &&
                                                       m.ClientId == "octo-pipeline-sa" && m.StatusClass == "2xx");
        Assert.DoesNotContain(_measurements.Snapshot(),
            m => m.ClientId.Contains(TestCaller.TenantId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Counter_WithoutToken_CountsAnonymousWithStatusClass()
    {
        await GetAsync($"/{TestCaller.TenantId}/v1/users/jane/explode");

        Assert.Contains(_measurements.Snapshot(), m => m.Endpoint == "GET /{tenantId}/v1/users/{userName}/explode" &&
                                                       m.ClientId == "anonymous" && m.StatusClass == "5xx");
    }

    [Fact]
    public async Task Counter_NeverCarriesTenantUserOrSubject()
    {
        await GetAsync($"/{TestCaller.TenantId}/v1/users/{Uri.EscapeDataString(TestCaller.UserName)}/password-state",
            authenticated: true);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/{TestCaller.TenantId}/v1/users/{Uri.EscapeDataString(TestCaller.UserName)}/reset-password");
        request.Headers.Add(TestCaller.AuthenticateHeader, "1");
        request.Headers.Add(TestCaller.RoleHeader, "admin");
        var reset = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var measurements = _measurements.Snapshot();
        Assert.NotEmpty(measurements);
        foreach (var value in measurements.SelectMany(m => m.Tags.Values))
        {
            Assert.DoesNotContain(TestCaller.TenantId, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestCaller.UserName, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("jane", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestCaller.Subject, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, bool authenticated = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authenticated)
        {
            request.Headers.Add(TestCaller.AuthenticateHeader, "1");
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertDeprecationHeaders(HttpResponseMessage response)
    {
        Assert.Equal(TestDeprecation.DeprecationHeader, Assert.Single(response.Headers.GetValues("Deprecation")));
        Assert.Equal(TestDeprecation.SunsetHeader, Assert.Single(response.Headers.GetValues("Sunset")));
        Assert.Equal(TestDeprecation.LinkHeader, Assert.Single(response.Headers.GetValues("Link")));
    }
}

/// <summary>Collects the measurements of <c>octo.rest.deprecated.calls</c> while it lives.</summary>
internal sealed class MeasurementCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public MeasurementCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RestDeprecationMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != RestDeprecationMetrics.CallsName)
            {
                return;
            }

            var dictionary = new Dictionary<string, string>();
            foreach (var tag in tags)
            {
                dictionary[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            }

            _measurements.Enqueue(new Measurement(value, dictionary));
        });
        _listener.Start();
    }

    public IReadOnlyList<Measurement> Snapshot() => _measurements.ToArray();

    public void Dispose() => _listener.Dispose();

    internal sealed record Measurement(long Value, IReadOnlyDictionary<string, string> Tags)
    {
        public string Endpoint => Tags.GetValueOrDefault("endpoint", string.Empty);
        public string ClientId => Tags.GetValueOrDefault("client_id", string.Empty);
        public string StatusClass => Tags.GetValueOrDefault("status_class", string.Empty);
    }
}
