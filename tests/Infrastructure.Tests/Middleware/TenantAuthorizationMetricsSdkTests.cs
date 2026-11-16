using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     The series budget of <c>octo.tenant_auth.decisions</c> against the OpenTelemetry SDK itself: more distinct
///     client × route refusals than the SDK default cardinality limit (2000 per metric) must not reach the SDK's
///     overflow series, and every refusal must stay countable by outcome.
/// </summary>
public class TenantAuthorizationMetricsSdkTests
{
    private const string OverflowTag = "otel.metric.overflow";

    [Fact]
    public void MoreCombinationsThanTheSdkLimit_NoOverflowSeries_AndEveryRefusalIsCounted()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(TenantGenerationMetrics.MeterName)
            .AddInMemoryExporter(exported)
            .Build();
        using var metrics = new TenantAuthorizationMetrics();

        var routes = new[] { Endpoint("{tenantId}/graphql"), Endpoint("{tenantId}/v1/tenants/backup") };
        const int clients = 1000;
        for (var i = 0; i < clients; i++)
        {
            foreach (var route in routes)
            {
                metrics.RecordServiceToken(TenantAuthorizationOutcome.Denied, ServiceTokenTenantEnforcementMode.Enforce,
                    $"sdk-client-{i}", route);
            }

            metrics.RecordServiceToken(TenantAuthorizationOutcome.Match, ServiceTokenTenantEnforcementMode.Enforce,
                $"sdk-client-{i}", routes[0]);
        }

        provider.ForceFlush();

        var points = new List<MetricPoint>();
        foreach (var metric in exported.Where(m => m.Name == TenantAuthorizationMetrics.CounterName))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                points.Add(point);
            }
        }

        // Other tests of this assembly may record on the same meter name in parallel; count only this test's series.
        long denied = 0, matched = 0;
        var series = 0;
        foreach (var point in points)
        {
            var tags = Tags(point);
            Assert.False(tags.ContainsKey(OverflowTag), "a measurement reached the SDK overflow series");
            if (tags.GetValueOrDefault(TenantAuthorizationMetrics.TokenKindTag) != "service" ||
                tags.GetValueOrDefault(TenantAuthorizationMetrics.ModeTag) != "enforce" ||
                !IsThisTestsClient(tags.GetValueOrDefault(TenantAuthorizationMetrics.ClientIdTag)))
            {
                continue;
            }

            series++;
            switch (tags[TenantAuthorizationMetrics.OutcomeTag])
            {
                case "denied":
                    denied += point.GetSumLong();
                    break;
                case "match":
                    matched += point.GetSumLong();
                    break;
            }
        }

        Assert.Equal(clients * routes.Length, denied);
        Assert.Equal(clients, matched);
        Assert.True(series <= TenantAuthorizationMetrics.MaxSeries + 2, $"{series} series");
    }

    private static bool IsThisTestsClient(string? clientId) =>
        clientId != null && (clientId.StartsWith("sdk-client-", StringComparison.Ordinal) ||
                             clientId == TenantGenerationMetrics.OtherClientId);

    private static Dictionary<string, string?> Tags(MetricPoint point)
    {
        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in point.Tags)
        {
            tags[tag.Key] = tag.Value?.ToString();
        }

        return tags;
    }

    private static RouteEndpoint Endpoint(string template) =>
        new(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty,
            template);
}
