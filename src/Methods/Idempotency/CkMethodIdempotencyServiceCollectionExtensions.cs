using Meshmakers.Octo.Services.Methods.Idempotency;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the idempotency store contract (AB#7817).</summary>
public static class CkMethodIdempotencyServiceCollectionExtensions
{
    /// <summary>
    ///     Binds <see cref="CkMethodIdempotencyOptions" /> (<c>Methods:Idempotency</c>), validates them at the start of the
    ///     service (a zero or negative <c>Ttl</c> fails it) and registers <see cref="CkMethodIdempotencyLifetime" />. A store
    ///     implementation calls this and registers itself.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional override of the configuration section.</param>
    public static IServiceCollection AddCkMethodIdempotencyOptions(this IServiceCollection services,
        Action<CkMethodIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<CkMethodIdempotencyOptions>()
            .BindConfiguration(CkMethodIdempotencyOptions.SectionName);
        if (configure != null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CkMethodIdempotencyOptions>, CkMethodIdempotencyOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CkMethodIdempotencyLifetime>();
        return services;
    }

    /// <summary>
    ///     Registers the in-memory store (<see cref="CkMethodInMemoryIdempotencyStore" />) unless a store is already
    ///     registered. For tests and single-replica hosts only: the state is not shared between replicas.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional override of the configuration section.</param>
    public static IServiceCollection AddCkMethodInMemoryIdempotencyStore(this IServiceCollection services,
        Action<CkMethodIdempotencyOptions>? configure = null)
    {
        services.AddCkMethodIdempotencyOptions(configure);
        services.TryAddSingleton<ICkMethodIdempotencyStore, CkMethodInMemoryIdempotencyStore>();
        return services;
    }
}
