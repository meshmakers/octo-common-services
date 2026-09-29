using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meshmakers.Octo.Services.Observability;

internal class ObservabilityBuilder(
    IConfigurationManager config,
    IServiceCollection services,
    IHostEnvironment environment)
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
        otel.WithMetrics(metrics =>
        {
            metrics
            // Metrics provider from OpenTelemetry
            .AddAspNetCoreInstrumentation()
            // Metrics provided by ASP.NET Core in .NET 8
            .AddMeter("Microsoft.AspNetCore.Hosting")
            .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
            // MongoDB command observability — emitted by MongoCommandObservability in
            // octo-construction-kit-engine-mongodb. Meter name kept as a string to avoid
            // cross-layer reference into the MongoDB engine.
            .AddMeter("Meshmakers.Octo.MongoDb")
            // On-demand workload lifecycle (AB#4919) — emitted by WorkloadLifecycleMetrics in
            // octo-communication-controller-services. Same rule: a string, not a reference into a
            // service. Registering it here for every service is harmless — a service that emits
            // nothing on this meter simply exports nothing.
            .AddMeter("Meshmakers.Octo.Communication")
            .AddPrometheusExporter();

            // AB#5430: metrics had a Prometheus scrape endpoint and nothing else, while tracing
            // got an OTLP exporter. Nothing scrapes that endpoint — kube-prometheus-stack was
            // replaced by Dash0 and no pod carries a prometheus.io/scrape annotation — so every
            // custom metric this platform emits was produced correctly and then dropped on the
            // floor. That is why octo.workload.* (AB#4919) never appeared in Dash0 and why the
            // OctoMeshWorkload* check rules could never fire. The Prometheus exporter stays:
            // removing it is a separate decision and would break anything still scraping.
            // Guarded by the same endpoint check the tracing path uses, so a service without
            // OTLP_ENDPOINT_URL configured keeps behaving exactly as before.
            // The endpoint comes from one of two places, and the order matters. An explicit
            // OTLP_ENDPOINT_URL wins where someone set it. Where nobody did — which is every
            // cluster today — fall back to OTEL_EXPORTER_OTLP_ENDPOINT, the standard variable
            // the Dash0 operator already injects into every pod, and let the SDK read endpoint
            // and protocol from the environment itself.
            //
            // The first attempt at this fix guarded on OTLP_ENDPOINT_URL alone and was therefore
            // dead on arrival: the key is set nowhere in the cluster. The tracing path below has
            // the same dead guard and has always had it — traces reach Dash0 through the
            // LD_PRELOAD auto-instrumentation injector, not through this exporter, which is why
            // nobody noticed. The injector does not know our custom meters, so metrics need the
            // in-process exporter that this adds.
            if (!string.IsNullOrWhiteSpace(tracingOtlpEndpoint))
            {
                metrics.AddOtlpExporter(otlpOptions => { otlpOptions.Endpoint = new Uri(tracingOtlpEndpoint); });
            }
            else if (!string.IsNullOrWhiteSpace(Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                metrics.AddOtlpExporter();
            }
        });

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

        // Surface per-request MongoDB cost back to the caller as response headers — paired
        // with the GraphQL extension listener in asset-repo-services for the same data on the
        // GraphQL surface.
        Services.AddTransient<IStartupFilter, MongoCommandSurfaceStartupFilter>();

        Services.AddHostedService<StartupBackgroundService>();
        Services.AddSingleton<StartupHealthCheck>();

        // This is a workaround to register the LinuxUtilizationParserCgroupV2 implementation for ILinuxUtilizationParser
        // because the Microsoft.Extensions.Diagnostics.ResourceMonitoring package does not register it by default.
        // See https://github.com/dotnet/extensions/issues/6832
        if (OperatingSystem.IsLinux())
        {
            Services.Add(ServiceDescriptor.Singleton(
                service           : Type.GetType("Microsoft.Extensions.Diagnostics.ResourceMonitoring.Linux.ILinuxUtilizationParser, Microsoft.Extensions.Diagnostics.ResourceMonitoring")!,
                implementationType: Type.GetType("Microsoft.Extensions.Diagnostics.ResourceMonitoring.Linux.LinuxUtilizationParserCgroupV2, Microsoft.Extensions.Diagnostics.ResourceMonitoring")!
            ));
        }

        var healthChecksBuilder = Services.AddHealthChecks()
            .AddCheck<StartupHealthCheck>(
                "Startup",
                tags: ["ready"]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            healthChecksBuilder
                .AddResourceUtilizationHealthCheck();
        }

        return healthChecksBuilder;
    }
}