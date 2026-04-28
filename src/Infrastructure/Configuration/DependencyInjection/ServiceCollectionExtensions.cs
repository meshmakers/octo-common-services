using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.ServiceClient.Authorization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Infrastructure.Cors;
using Meshmakers.Octo.Services.Infrastructure.DistributionEventHub;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Cors.Infrastructure;
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
    public static IOctoInfrastructureBuilder AddOctoServiceInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITenantNotifications, DistributedTenantNotifications>();
        // Replace the engine-default LoggingArchiveAuditTrail with the event-bus version so
        // archive lifecycle transitions (concept §14) get published over the distribution event
        // hub. Logging stays available via the engine's default for projects that don't add
        // octo service infrastructure.
        services.AddSingleton<IArchiveAuditTrail, EventBusArchiveAuditTrail>();
        services.TryAddSingleton<IDistributedCacheService, DistributedCacheService>();
        services.AddSingleton<IRepositoryClient, OctoRepositoryClient>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IOctoHttpContextAccessor, OctoHttpContextAccessor>();
        services.AddScoped<IMultiTenancyResolverService, MultiTenancyResolverService>();
        services.AddScoped<IKnownOriginsProvider, KnownOriginsProvider>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();
        services.AddSingletonMultipleInterfaces<CorsPolicyProvider, ICorsPolicyProvider>();
        services.AddExceptionHandler<OctoExceptionHandler>();
        return new OctoInfrastructureBuilder(services);
    }

    /// <summary>
    ///     Adds infrastructure components to all octo services
    /// </summary>
    /// <param name="services"></param>
    /// <param name="uniqueBrokerServiceAddress">A unique address for the distribution event hub</param>
    /// <param name="configureDistributionEventHub">Optional configuration of the distribution event hub</param>
    public static IOctoInfrastructureBuilder AddOctoServiceInfrastructure(this IServiceCollection services, string uniqueBrokerServiceAddress,
        Action<IDistributionEventHubConfiguration>? configureDistributionEventHub = null)
    {
        var builder = AddOctoServiceInfrastructure(services);

        // Adding dependent octo modules
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = uniqueBrokerServiceAddress;

            // Defer bus startup until after tenant initialization is complete.
            // The bus is started by EventHubStartupService (Order = 20) after
            // DefaultConfigurationInitializationService (Order = 10) completes.
            // This prevents MongoDB lock contention caused by message consumers
            // processing tenant events while the initial tenant setup is still running.
            c.AutomaticallyStartBusDuringStartup = false;

            configureDistributionEventHub?.Invoke(c);

            c.AddBroadcastEventConsumer<CorsClientsUpdateConsumer, CorsClientsUpdate>();
            c.AddBroadcastEventConsumer<PreUpdatePreDeleteTenantConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<PreUpdatePreDeleteTenantConsumer, PreDeleteTenant>();
            c.AddBroadcastEventConsumer<PosCreatePosUpdateTenantConsumer, PosCreateTenant>();
            c.AddBroadcastEventConsumer<PosCreatePosUpdateTenantConsumer, PosUpdateTenant>();
        });

        // Initialization order:
        // 1. DefaultConfigurationInitializationService (Order=10): Setup tenants (CK model import, identity data, migrations)
        //    - Sets DeferTenantStart=true so StartTenantAsync is NOT called during setup
        // 2. EventHubStartupService (Order=20): Start the distribution event hub
        // 3. TenantStartupInitializationService (Order=30): Call StartTenantAsync for all deferred tenants
        //    - Now safe because the bus is available for sending commands
        services.AddInitializationService<DefaultConfigurationInitializationService>();
        services.AddInitializationService<EventHubStartupService>();
        services.AddInitializationService<TenantStartupInitializationService>();

        // Background retry for tenants that failed during deferred startup
        services.AddSingleton<FailedTenantRegistry>();
        services.AddHostedService<FailedTenantRetryBackgroundService>();

        return builder;
    }

    /// <summary>
    ///     Adds user info middleware components
    /// </summary>
    /// <param name="builder"></param>
    public static void AddAuthorizationUserInfo(this IOctoInfrastructureBuilder builder)
    {
        builder.Services.AddSingleton<IUserInfoCache, UserInfoCache>();
        builder.Services.AddSingleton<IAuthorizationClient, AuthorizationClient>();
    }
}