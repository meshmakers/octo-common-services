using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the CK method dispatcher (the calling side of CK methods).</summary>
public static class CkMethodDispatcherServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="ICkMethodDispatcher" /> and the handler registry behind it. Requires the distribution
    ///     event hub with <see cref="AddCkMethodHandlerRegistryFeed" /> in its configuration and, to resolve methods of
    ///     derived or implementing types, the CK cache. Methods registered with <c>AddCkInProcessMethodHandlers</c> in the
    ///     same service are called in-process; the others go over the bus. The invocation pipeline is shared with the
    ///     in-process transport, register filters with <c>AddCkMethodInvocationFilter</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddCkMethodDispatcher(this IServiceCollection services)
    {
        services.AddOptions<CkMethodDispatcherOptions>().BindConfiguration(CkMethodDispatcherOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.AddCkMethodInvocationPipeline();
        services.TryAddSingleton<CkMethodHandlerRegistry>();
        services.TryAddSingleton<ICkMethodHandlerRegistry>(sp => sp.GetRequiredService<CkMethodHandlerRegistry>());
        services.TryAddSingleton<ICkMethodDeclaringElementResolver, CkCacheMethodDeclaringElementResolver>();
        services.TryAddSingleton<ICkMethodDispatcher, CkMethodDispatcher>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodDispatcherStartupService)))
        {
            services.AddInitializationService<CkMethodDispatcherStartupService>();
        }

        return services;
    }

    /// <summary>
    ///     Feeds the handler registry of the dispatcher from the announce and withdraw broadcasts of the handler hosts.
    ///     Call it in the <c>AddDistributionEventHub</c> configuration of every service that registers
    ///     <see cref="AddCkMethodDispatcher" />.
    /// </summary>
    public static void AddCkMethodHandlerRegistryFeed(this IDistributionEventHubConfiguration configuration)
    {
        configuration.AddBroadcastEventConsumer<CkMethodRegistryConsumer, CkMethodHandlerAnnounced>();
        configuration.AddBroadcastEventConsumer<CkMethodRegistryConsumer, CkMethodHandlerWithdrawn>();
    }
}
