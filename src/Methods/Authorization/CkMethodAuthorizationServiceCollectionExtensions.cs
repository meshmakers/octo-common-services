using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the CK method authorization (F3.4).</summary>
public static class CkMethodAuthorizationServiceCollectionExtensions
{
    /// <summary>
    ///     Adds <see cref="CkMethodAuthorizationFilter" /> to the invocation pipeline (band 200) through
    ///     <c>AddCkMethodInvocationFilter</c> and registers the shared <see cref="ICkMethodAuthorizationEvaluator" />, the
    ///     ambient <see cref="ICkMethodPrincipalProvider" /> (<see cref="CkMethodPrincipalScope" />) and the CK-cache
    ///     definition source (needs <c>ICkCacheService</c>). The services use <c>TryAdd</c>, so a service can replace the
    ///     principal provider or the definition source before or after this call. Safe to call once per service; a second
    ///     call would add the filter twice.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options (administrator roles); also bound from <c>Methods:Authorization</c>.</param>
    public static IServiceCollection AddCkMethodAuthorization(this IServiceCollection services,
        Action<CkMethodAuthorizationOptions>? configure = null)
    {
        var options = services.AddOptions<CkMethodAuthorizationOptions>()
            .BindConfiguration(CkMethodAuthorizationOptions.SectionName);
        if (configure != null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<ICkMethodAuthorizationEvaluator, CkMethodAuthorizationEvaluator>();
        services.TryAddSingleton<ICkMethodPrincipalProvider, CkMethodPrincipalScope>();
        services.TryAddSingleton<ICkMethodAuthorizationDefinitionSource, CkCacheMethodAuthorizationDefinitionSource>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodAuthorizationFilter)))
        {
            services.AddCkMethodInvocationFilter<CkMethodAuthorizationFilter>();
        }

        return services;
    }
}
