using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extensions for dependency injection's service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds infrastructure components to all octo services
    /// </summary>
    /// <param name="services"></param>
    /// <param name="uniqueBrokerServiceAddress"></param>
    /// <param name="configureDistributionEventHub"></param>
    public static void AddOctoServiceInfrastructure(this IServiceCollection services, string uniqueBrokerServiceAddress, Action<IDistributionEventHubConfiguration>? configureDistributionEventHub = null)
    {
        // Adding dependent octo modules
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = uniqueBrokerServiceAddress;
        
            configureDistributionEventHub?.Invoke(c);
            
            c.AddBroadcastEventConsumer<CorsClientsUpdateConsumer, CorsClientsUpdate>();
            c.AddBroadcastEventConsumer<PreUpdateTenantConsumer, PreUpdateTenant>();
        });
    }
}