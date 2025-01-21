using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Extensions for dependency injection's service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds infrastructure components to all octo services
    /// </summary>
    /// <param name="services"></param>
    public static void AddOctoServiceInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITenantNotifications, DistributedTenantNotifications>();
        services.TryAddSingleton<IDistributedCacheService, DistributedCacheService>();
        services.AddSingleton<IRepositoryClient, OctoRepositoryClient>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<IMultiTenancyResolverService, MultiTenancyResolverService>();
        services.AddScoped<IKnownOriginsProvider, KnownOriginsProvider>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();
        services.AddSingletonMultipleInterfaces<CorsPolicyProvider, ICorsPolicyProvider>();
    }

    /// <summary>
    ///     Adds infrastructure components to all octo services
    /// </summary>
    /// <param name="services"></param>
    /// <param name="uniqueBrokerServiceAddress">A unique address for the distribution event hub</param>
    /// <param name="configureDistributionEventHub">Optional configuration of the distribution event hub</param>
    public static void AddOctoServiceInfrastructure(this IServiceCollection services, string uniqueBrokerServiceAddress,
        Action<IDistributionEventHubConfiguration>? configureDistributionEventHub = null)
    {
        AddOctoServiceInfrastructure(services);

        // Adding dependent octo modules
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = uniqueBrokerServiceAddress;

            configureDistributionEventHub?.Invoke(c);

            c.AddBroadcastEventConsumer<CorsClientsUpdateConsumer, CorsClientsUpdate>();
            c.AddBroadcastEventConsumer<CacheTenantConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<CacheTenantConsumer, PreDeleteTenant>();
            c.AddBroadcastEventConsumer<PosCreateTenantConsumer, PosCreateTenant>();
        });

        services.AddInitializationService<DefaultConfigurationInitializationService>();
    }
}