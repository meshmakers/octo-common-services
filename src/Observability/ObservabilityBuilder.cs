using System;
using Meshmakers.Octo.Runtime.Engine.Secrets;
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

        // AB#5478 §2.1: one process must report ONE service.name, and the value that wins has to
        // be the deployment's, not ours.
        //
        // Three producers attach a service.name to the signals of a single pod, and until this
        // guard they disagreed (measured in the test-2 dataset on 2026-10-02, all three on the
        // one Deployment octo-mesh-identity-services):
        //
        //   metrics  Meshmakers.Octo.Backend.IdentityServices   <- this AddService() call
        //   spans    octo-mesh-identity-services                <- OTEL_SERVICE_NAME, read by the
        //                                                          injected auto-instrumentation
        //   logs     octo-mesh                                  <- pod label app.kubernetes.io/name,
        //                                                          read by the collector off the node
        //
        // An explicit AddService() beats the OTEL_SERVICE_NAME environment variable, so this line
        // was what split metrics off from traces: the Dash0 service catalog grew a second, parallel
        // entity per service carrying only our custom metrics and no RED data, and no dashboard
        // could put a service's latency next to its own error logs.
        //
        // So we feed the deployment's name INTO AddService rather than dropping the call. Dropping it
        // would let the SDK's default resource read OTEL_SERVICE_NAME on its own and would look
        // tidier — but AddService is also the only thing in this estate that produces
        // service.instance.id (autoGenerateServiceInstanceId defaults to true; the Dash0 operator
        // sets it on no signal). Today that is the one identity attribute that IS correct, and
        // removing the call would silently drop it.
        //
        // ApplicationName (the entry assembly name) stays the fallback for anything running without
        // the variable — local dev, tests, every host the charts do not reach — so off-cluster
        // behaviour is unchanged.
        //
        // Read through IConfiguration, not Environment.GetEnvironmentVariable: that is the same
        // source the SDK itself uses for this key, so an appsettings override behaves the same way
        // as the pod env. Length check rather than ?? — IConfiguration treats an empty value as set,
        // and AddService throws on an empty serviceName (Guard.ThrowIfNullOrEmpty).
        //
        // This fixes metrics only. Logs carry a third name (the pod label) that no code in this
        // process can reach — the collector reads it off the node and never sees our env. That half
        // lives in the Dash0Monitoring transform, see AB#5478 §2.1.
        var deploymentServiceName = Configuration["OTEL_SERVICE_NAME"];
        otel.ConfigureResource(resource => resource
            .AddService(serviceName: deploymentServiceName is { Length: > 0 }
                ? deploymentServiceName
                : Environment.ApplicationName));

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
            // CK model health (AB#5432) — emitted by CkModelObservabilityMetrics in
            // octo-asset-repo-services. Same rule again: a string, not a reference into a service.
            // Note that asset-repo ALSO registers this meter on its own meter provider, and must:
            // this list only reaches a service once it consumes a build of this package that
            // contains the line, and an unregistered meter fails silently — instruments are
            // created, measurements are taken, and every one of them is dropped. That is the
            // AB#5430 failure mode, and a metric contract three deployed check rules depend on
            // cannot wait a release train to find out.
            .AddMeter("Meshmakers.Octo.AssetRepository")
            // StreamData (concept §13) — engine-side archive lifecycle signals from
            // StreamDataDiagnostics in octo-construction-kit-engine, and the CrateDB data-plane
            // signals from CrateDbDiagnostics in octo-construction-kit-engine-mongodb. Both have
            // existed and been emitting since the StreamData work, and neither was ever
            // registered here: nine instruments across the two meters, every measurement
            // dropped. Same silent-drop failure as AB#5430, found by auditing the meter names
            // in the tree against this list rather than by anything going wrong — which is the
            // point: an unregistered meter has no symptom to notice.
            .AddMeter("Meshmakers.Octo.StreamData")
            .AddMeter("Meshmakers.Octo.StreamData.Crate")
            // SECRET attribute access (AB#5528/AB#5531) — octo.secrets.decrypt and
            // octo.secrets.plaintext_reads from SecretDiagnostics in octo-construction-kit-engine.
            // A constant reference rather than a string, unlike the meters above: the engine is
            // already a dependency of this package (via Runtime.Engine.MongoDb), so the reference
            // crosses no layer, and a rename on the engine side then fails the build here instead
            // of silently dropping every measurement (the AB#5430 failure mode).
            .AddMeter(SecretDiagnostics.MeterName)
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

        // Add Tracing for ASP.NET Core and our custom ActivitySources
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();

            // Our own ActivitySources. Same string-not-reference rule as the meters above, and
            // the same failure mode: an ActivitySource nobody subscribed to has no listener, so
            // Activity.StartActivity() returns null and the span is never created at all. These
            // two have been defined since the StreamData work with no AddSource anywhere in the
            // tree — the comment on StreamDataDiagnostics.ActivitySourceName even says exporters
            // "need a single subscription", and nothing ever made it.
            tracing.AddSource("Meshmakers.Octo.StreamData");
            tracing.AddSource("Meshmakers.Octo.StreamData.Crate");

            // DELIBERATELY NOT given the OTEL_EXPORTER_OTLP_ENDPOINT fallback that the metrics
            // path above has. The guard below is dead — nothing in any cluster sets
            // OTLP_ENDPOINT_URL — but here that is the correct state, not an oversight:
            //
            // every monitored pod already runs opentelemetry-dotnet-instrumentation 1.11.0,
            // injected by the Dash0 operator, and that is where today's AspNetCore and
            // HttpClient spans come from (verified in the test-2 dataset: every SERVER span
            // carries telemetry.distro.name=opentelemetry-dotnet-instrumentation). The injector
            // builds its own TracerProvider with its own exporter. Giving this one an exporter
            // too would not add the StreamData spans — it would duplicate every HTTP span in
            // the estate, doubling span volume against the cost guardrails and splitting each
            // request into two trace trees.
            //
            // The metrics path has no such conflict: the injector does not know our meters, so
            // its exporter is the only one for HTTP metrics and ours the only one for custom
            // metrics (that asymmetry is exactly why AB#5430 fixed metrics this way).
            //
            // The AddSource calls above still earn their place. An ActivitySource with no
            // listener never creates an Activity at all, so they are what makes the spans exist
            // for the injector's provider to pick up — the injector is told about them via
            // OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES in the charts. They also make the
            // in-process path work unchanged for anyone who does set OTLP_ENDPOINT_URL, i.e.
            // local dev against a Jaeger/collector with no injector in the picture.
            if (!string.IsNullOrWhiteSpace(tracingOtlpEndpoint))
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