using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of CK method handlers that run in the same process as their caller (no bus hop).</summary>
public static class CkInProcessMethodServiceCollectionExtensions
{
    /// <summary>
    ///     Registers handlers for methods that this service owns and calls itself. The handler classes are the same typed
    ///     handlers as for <c>AddCkMethodHandlers</c> (one class works under either transport). In-process handlers are not
    ///     announced, have no bus queue and need no event hub. Inject <see cref="ICkInProcessMethodTransport" /> to call them.
    ///     A method is registered in exactly one of the two places; startup fails otherwise.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Registers one handler per method: <c>m.Handle&lt;MyHandler&gt;(MethodId)</c>.</param>
    public static IServiceCollection AddCkInProcessMethodHandlers(this IServiceCollection services,
        Action<ICkMethodHandlerRegistrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var registrations = services.FirstOrDefault(d => d.ServiceType == typeof(CkInProcessMethodRegistrations))
            ?.ImplementationInstance as CkInProcessMethodRegistrations;
        if (registrations == null)
        {
            registrations = new CkInProcessMethodRegistrations();
            services.AddSingleton(registrations);
        }

        configure(new RegistrationBuilder(services, registrations));

        services.AddOptions<CkMethodHandlerOptions>().BindConfiguration(CkMethodHandlerOptions.SectionName);
        services.AddOptions<CkMethodLimitsOptions>().BindConfiguration(CkMethodLimitsOptions.SectionName);
        services.AddOptions<CkMethodDispatcherOptions>().BindConfiguration(CkMethodDispatcherOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICkMethodDefinitionSource, CkCacheMethodDefinitionSource>();
        services.TryAddSingleton<CkMethodRegistrationValidator>();
        services.AddCkMethodInvocationPipeline();
        services.AddOptions<CkMethodAuthorizationOptions>().BindConfiguration(CkMethodAuthorizationOptions.SectionName);
        services.TryAddSingleton<ICkMethodAuthorizationEvaluator, CkMethodAuthorizationEvaluator>();
        services.TryAddSingleton<CkInProcessMethodTransport>();
        services.TryAddSingleton<ICkInProcessMethodTransport>(sp => sp.GetRequiredService<CkInProcessMethodTransport>());
        if (services.All(d => d.ImplementationType != typeof(CkInProcessMethodValidationService)))
        {
            services.AddInitializationService<CkInProcessMethodValidationService>();
        }

        return services;
    }

    /// <summary>
    ///     Registers a tripwire probe (<see cref="ICkInProcessAmbientStateProbe" />) for the in-process transport: when it
    ///     reports request-bound ambient state at handler entry, the call fails with <c>METHOD_HANDLER_FAILED</c> and the
    ///     handler does not run. Any number of probes may be registered; the probe is a singleton.
    /// </summary>
    public static IServiceCollection AddCkInProcessAmbientStateProbe<TProbe>(this IServiceCollection services)
        where TProbe : class, ICkInProcessAmbientStateProbe
    {
        services.AddSingleton<ICkInProcessAmbientStateProbe, TProbe>();
        return services;
    }

    /// <summary>
    ///     Registers the invocation pipeline (<see cref="ICkMethodInvocationPipeline" />) that the in-process transport and
    ///     the bus dispatcher both run, and the <see cref="ICkMethodSensitivityResolver" /> behind the redacted request view
    ///     (it uses the registered <c>ICkMethodAuthorizationDefinitionSource</c>, else the CK cache; with neither, every
    ///     parameter value is redacted). Idempotent; called by <see cref="AddCkInProcessMethodHandlers" />.
    /// </summary>
    public static IServiceCollection AddCkMethodInvocationPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton<ICkMethodSensitivityResolver>(sp => new CkMethodSensitivityResolver(
            sp.GetService<ICkMethodAuthorizationDefinitionSource>() ??
            (sp.GetService<ICkCacheService>() is { } ckCache
                ? new CkCacheMethodAuthorizationDefinitionSource(ckCache,
                    sp.GetRequiredService<ILogger<CkCacheMethodAuthorizationDefinitionSource>>())
                : null)));
        services.TryAddSingleton<ICkMethodInvocationPipeline, CkMethodInvocationPipeline>();
        return services;
    }

    /// <summary>
    ///     Adds a step to the invocation pipeline (authorization, audit, idempotency). The filter is a singleton; it must
    ///     be thread-safe and take scoped services from a scope it creates itself.
    /// </summary>
    public static IServiceCollection AddCkMethodInvocationFilter<TFilter>(this IServiceCollection services)
        where TFilter : class, ICkMethodInvocationFilter
    {
        services.AddCkMethodInvocationPipeline();
        services.AddSingleton<ICkMethodInvocationFilter, TFilter>();
        return services;
    }

    private sealed class RegistrationBuilder(IServiceCollection services, CkMethodHandlerRegistrations registrations)
        : ICkMethodHandlerRegistrationBuilder
    {
        public ICkMethodHandlerRegistrationBuilder Handle<THandler>(string qualifiedMethodId)
            where THandler : class, ICkMethodHandler
        {
            registrations.Add(qualifiedMethodId, typeof(THandler));
            services.TryAddScoped<THandler>();
            return this;
        }
    }
}
