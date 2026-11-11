using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the operation runtime of asynchronous CK methods (AB#8443).</summary>
public static class CkMethodOperationServiceCollectionExtensions
{
    /// <summary>
    ///     Binds <see cref="CkMethodOperationOptions" /> (<c>Methods:Operations</c>) and validates them at the start of the
    ///     service (retention 1 h to 30 d; an invalid value stops the host, the message names the setting only). A store
    ///     implementation calls this.
    /// </summary>
    public static IServiceCollection AddCkMethodOperationOptions(this IServiceCollection services,
        Action<CkMethodOperationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<CkMethodOperationOptions>()
            .BindConfiguration(CkMethodOperationOptions.SectionName);
        if (configure != null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<CkMethodOperationOptions>, CkMethodOperationOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>
    ///     Lets the handlers of this host accept calls as long-running operations
    ///     (<see cref="CkMethodOutcome.Accepted" />): the runner that records and runs them, the heartbeat of the running
    ///     operations and the sweep that ends lost ones. The host also registers a store
    ///     (<c>AddCkMethodMongoOperationStore()</c>, or <see cref="AddCkMethodInMemoryOperationStore" /> for tests).
    ///     Without this registration a handler that returns <c>Accepted</c> is answered <c>METHOD_HANDLER_FAILED</c>.
    /// </summary>
    public static IServiceCollection AddCkMethodOperations(this IServiceCollection services,
        Action<CkMethodOperationOptions>? configure = null)
    {
        services.AddCkMethodOperationSweep(configure);
        services.TryAddSingleton<CkMethodOperationHost>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodOperationHeartbeatService)))
        {
            services.AddHostedService<CkMethodOperationHeartbeatService>();
        }

        return services;
    }

    /// <summary>
    ///     Only the sweep that ends lost operations (no heartbeat for <c>StaleAfter</c>), for a host that reads operations
    ///     but runs none (the gateway). Safe to call together with <see cref="AddCkMethodOperations" />.
    /// </summary>
    public static IServiceCollection AddCkMethodOperationSweep(this IServiceCollection services,
        Action<CkMethodOperationOptions>? configure = null)
    {
        services.AddCkMethodOperationOptions(configure);
        services.TryAddSingleton<CkMethodOperationRunner>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodOperationSweepService)))
        {
            services.AddHostedService<CkMethodOperationSweepService>();
        }

        return services;
    }

    /// <summary>
    ///     Registers <see cref="CkMethodInMemoryOperationStore" /> unless a store is registered. Tests and single-replica
    ///     hosts only: the records are not shared and do not survive a restart.
    /// </summary>
    public static IServiceCollection AddCkMethodInMemoryOperationStore(this IServiceCollection services,
        Action<CkMethodOperationOptions>? configure = null)
    {
        services.AddCkMethodOperationOptions(configure);
        services.TryAddSingleton<ICkMethodOperationStore, CkMethodInMemoryOperationStore>();
        services.AddCkMethodOperationTenantPurge();
        return services;
    }

    /// <summary>
    ///     Registers <see cref="ICkMethodOperationTenantPurge" /> (AB#8447) unless one is registered: the registered
    ///     <see cref="ICkMethodOperationStore" /> when it implements the purge, else <see cref="CkMethodNoOperationTenantPurge" />.
    ///     Called by the store registrations; a host with its own store calls it after registering the store.
    /// </summary>
    public static IServiceCollection AddCkMethodOperationTenantPurge(this IServiceCollection services)
    {
        services.TryAddSingleton<ICkMethodOperationTenantPurge>(sp =>
            sp.GetRequiredService<ICkMethodOperationStore>() as ICkMethodOperationTenantPurge ??
            new CkMethodNoOperationTenantPurge());
        return services;
    }
}
