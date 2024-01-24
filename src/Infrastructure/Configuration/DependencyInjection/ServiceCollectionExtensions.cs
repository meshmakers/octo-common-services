using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;
using Meshmakers.Octo.Services.Infrastructure.Services;
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
    /// <param name="uniqueBrokerServiceAddress">A unique address for the distribution event hub</param>
    /// <param name="configureDistributionEventHub">Optional configuration of the distribution event hub</param>
    public static void AddOctoServiceInfrastructure(this IServiceCollection services, string uniqueBrokerServiceAddress,
        Action<IDistributionEventHubConfiguration>? configureDistributionEventHub = null)
    {
        services.AddSingleton<ITenantNotifications, DistributedTenantNotifications>();
        services.AddSingleton<IRepositoryClient, OctoRepositoryClient>();

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