using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration of the REST deprecation mechanism (AB#8421).
/// </summary>
public static class RestDeprecationServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the deprecation headers and the usage counter for actions marked with
    ///     <see cref="OctoRestDeprecatedAttribute" />. <c>AddObservability()</c> already calls it; calling it again is a
    ///     no-op. Export of the counter needs the meter <see cref="RestDeprecationMetrics.MeterName" /> on the meter
    ///     provider, which <c>AddObservability()</c> also does. The wave dates are bound from the configuration section
    ///     <see cref="RestDeprecationOptions.SectionName" /> (AB#8415).
    /// </summary>
    public static IServiceCollection AddOctoRestDeprecation(this IServiceCollection services)
    {
        services.AddOptions<RestDeprecationOptions>().BindConfiguration(RestDeprecationOptions.SectionName);
        services.TryAddSingleton<RestDeprecationResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, RestDeprecationStartupFilter>());
        return services;
    }
}
