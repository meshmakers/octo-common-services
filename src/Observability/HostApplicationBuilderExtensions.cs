using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Observability;

public static class HostApplicationBuilderExtensions
{
    /// <summary>
    /// Add observability to the host application
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IHealthChecksBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        return new ObservabilityBuilder(builder.Configuration, builder.Services, builder.Environment)
            .AddObservability();
    }
}