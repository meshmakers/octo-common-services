using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of CK method handlers (remote bus transport).</summary>
public static class CkMethodServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the handlers of this service: the host consumes the method queues, announces the handlers after the
    ///     endpoints are ready, heartbeats and withdraws on graceful shutdown. Requires the distribution event hub
    ///     (<c>AddDistributionEventHub</c> with <see cref="AddCkMethodHandlerDiscovery" />) and the CK cache.
    ///     May be called more than once with the same service name; method ids are unique per service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceName">Name of the service in the registry, e.g. <c>identity-services</c>.</param>
    /// <param name="configure">Registers one handler per method: <c>m.Handle&lt;MyHandler&gt;(MethodId)</c>.</param>
    public static IServiceCollection AddCkMethodHandlers(this IServiceCollection services, string serviceName,
        Action<ICkMethodHandlerRegistrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var registrations = GetOrAddRegistrations(services);
        registrations.SetServiceName(serviceName);
        configure(new RegistrationBuilder(services, registrations));

        services.AddOptions<CkMethodHandlerOptions>().BindConfiguration(CkMethodHandlerOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICkMethodDefinitionSource, CkCacheMethodDefinitionSource>();
        services.TryAddSingleton<CkMethodRegistrationValidator>();
        services.TryAddSingleton<CkMethodHandlerInstance>();
        services.TryAddSingleton<CkMethodHandlerInvoker>();
        services.TryAddSingleton<ICkMethodHandlerInvoker>(sp => sp.GetRequiredService<CkMethodHandlerInvoker>());
        services.TryAddSingleton<CkMethodBusConsumer>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodHandlerStartupService)))
        {
            services.AddInitializationService<CkMethodHandlerValidationService>();
            services.AddInitializationService<CkMethodHandlerStartupService>();
            services.AddHostedService<CkMethodHandlerHeartbeatService>();
        }

        return services;
    }

    /// <summary>
    ///     Lets this service answer discovery requests of dispatchers with an immediate announcement. Call it in the
    ///     <c>AddDistributionEventHub</c> configuration of every service that hosts method handlers.
    /// </summary>
    public static void AddCkMethodHandlerDiscovery(this IDistributionEventHubConfiguration configuration)
    {
        configuration.AddBroadcastEventConsumer<CkMethodHandlerDiscoveryConsumer, CkMethodHandlerDiscoveryRequested>();
    }

    private static CkMethodHandlerRegistrations GetOrAddRegistrations(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(CkMethodHandlerRegistrations))
            ?.ImplementationInstance as CkMethodHandlerRegistrations;
        if (existing != null)
        {
            return existing;
        }

        var registrations = new CkMethodHandlerRegistrations();
        services.AddSingleton(registrations);
        return registrations;
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
