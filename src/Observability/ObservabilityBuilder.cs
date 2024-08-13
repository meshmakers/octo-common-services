using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meshmakers.Octo.Services.Observability;

internal class ObservabilityBuilder(IConfigurationManager config, IServiceCollection services, IHostEnvironment environment)
{
    public IConfigurationManager Configuration { get; } = config;
    public IServiceCollection Services { get; } = services;
    public IHostEnvironment Environment { get; } = environment;
    internal IHealthChecksBuilder AddObservability()
    {
        var tracingOtlpEndpoint = Configuration["OTLP_ENDPOINT_URL"];
        var otel = Services.AddOpenTelemetry();

        // Configure OpenTelemetry Resources with the application name
        otel.ConfigureResource(resource => resource
            .AddService(serviceName: Environment.ApplicationName));

        // Add Metrics for ASP.NET Core and our custom metrics and export to Prometheus
        otel.WithMetrics(metrics => metrics
            // Metrics provider from OpenTelemetry
            .AddAspNetCoreInstrumentation()
            // Metrics provides by ASP.NET Core in .NET 8
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            .AddPrometheusExporter());

        // Add Tracing for ASP.NET Core and our custom ActivitySource and export to Jaeger
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();
            if (tracingOtlpEndpoint != null)
            {
                tracing.AddOtlpExporter(otlpOptions => { otlpOptions.Endpoint = new Uri(tracingOtlpEndpoint); });
            }
        });

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Services.AddResourceMonitoring();
        }

        return Services.AddHealthChecks()
            .AddResourceUtilizationHealthCheck();
    }
}