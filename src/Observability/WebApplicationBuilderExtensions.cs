using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Meshmakers.Octo.Services.Observability;

public static class WebApplicationBuilderExtensions
{
    public static IHealthChecksBuilder AddObservability(this WebApplicationBuilder builder)
    {
        return new ObservabilityBuilder(builder.Configuration, builder.Services, builder.Environment)
            .AddObservability();
    }
}