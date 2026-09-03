using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.DistributionEventHub.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Sdk.ServiceClient.Authorization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure.Consumers;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Infrastructure.Routing;
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
        services.TryAddSingleton<IDistributedCacheService, DistributedCacheService>();
        services.AddSingleton<IRepositoryClient, OctoRepositoryClient>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IOctoHttpContextAccessor, OctoHttpContextAccessor>();
        services.AddScoped<IMultiTenancyResolverService, MultiTenancyResolverService>();
        services.AddScoped<IKnownOriginsProvider, KnownOriginsProvider>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();
        services.AddSingleton<ITenantCapabilityStateReader, TenantCapabilityStateReader>();
        // Singleton because it owns the parent/child cache that keeps the parent-tenant
        // administration rule (AB#5060) off the database on every request.
        services.TryAddSingleton<ITenantHierarchyReader, TenantHierarchyReader>();
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
    ///     Binds <see cref="TenantAuthorizationOptions" /> from configuration section
    ///     <see cref="TenantAuthorizationOptions.SectionName" /> (AB#5032), so an operator can narrow
    ///     the client-credentials exemption of <c>UseOctoTenantAuthorization()</c> per environment.
    /// </summary>
    /// <remarks>
    ///     Optional. Without this call the middleware runs on the defaults, which reproduce the request
    ///     behaviour of every release before AB#5032 (service tokens pass) and only add the audit log.
    /// </remarks>
    public static IServiceCollection AddOctoTenantAuthorization(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TenantAuthorizationOptions>(
            configuration.GetSection(TenantAuthorizationOptions.SectionName));
        return services;
    }

    /// <summary>
    ///     Configures <see cref="TenantAuthorizationOptions" /> in code (AB#5032).
    /// </summary>
    public static IServiceCollection AddOctoTenantAuthorization(this IServiceCollection services,
        Action<TenantAuthorizationOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    /// <summary>
    ///     Registers <see cref="TenantIdRouteConstraint" /> under the key <c>tenantId</c>, so route
    ///     templates can be written as <c>{tenantId:tenantId}</c> (AB#5060).
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Every host that serves a tenant-addressed route must call this.</b> Without the
    ///     registration the constraint key is unknown and ASP.NET throws at startup — loudly, which is
    ///     the intended failure. The silent one to avoid is the opposite: dropping the constraint from
    ///     a template to make the error go away, which leaves the route matching segments that can
    ///     never name a tenant.
    ///     <para>
    ///         Until AB#5060 each host carried its own <c>internal</c> copy of the constraint and its
    ///         own <c>ConstraintMap.Add</c> line, and the copies had drifted apart. This is the single
    ///         registration they were all approximating.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddOctoTenantIdRouteConstraint(this IServiceCollection services)
    {
        services.Configure<RouteOptions>(options =>
            options.ConstraintMap[InfrastructureCommon.TenantIdRoute] = typeof(TenantIdRouteConstraint));
        return services;
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