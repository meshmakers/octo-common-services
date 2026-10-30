using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
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
    ///     service (a zero or negative <c>Ttl</c> fails it) and registers <see cref="CkMethodIdempotencyLifetime" />. Also binds the
    ///     fingerprint keys (<c>Methods:Idempotency:Fingerprint</c>, malformed keys fail the start) and registers the
    ///     <see cref="ICkMethodParametersFingerprinter" /> the pipeline uses (AB#7819). A store
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
        services.AddOptions<CkMethodFingerprintOptions>().BindConfiguration(CkMethodFingerprintOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CkMethodFingerprintOptions>, CkMethodFingerprintOptionsValidator>());
        // the pipeline fingerprints keyed calls only when this is registered (the idempotency feature is on)
        services.TryAddSingleton<ICkMethodParametersFingerprinter, CkMethodParametersFingerprinter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CkMethodIdempotencyOptions>, CkMethodIdempotencyOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CkMethodIdempotencyLifetime>();
        return services;
    }

    /// <summary>
    ///     Adds <see cref="CkMethodIdempotencyFilter" /> to the invocation pipeline (band 300, AB#7820) together with the options
    ///     and the fingerprinter (<see cref="AddCkMethodIdempotencyOptions" />). The host also registers a store
    ///     (<c>AddCkMethodInMemoryIdempotencyStore</c> for tests and single-replica hosts, else the MongoDB store) and the
    ///     authorization (<c>AddCkMethodAuthorization</c>, which supplies the <see cref="ICkMethodPrincipalProvider" />
    ///     the filter takes the caller subject from). Without a store the pipeline cannot be built: the host fails instead of
    ///     running keyed calls unprotected. Safe to call more than once; the filter is added once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional override of the configuration section.</param>
    public static IServiceCollection AddCkMethodIdempotency(this IServiceCollection services,
        Action<CkMethodIdempotencyOptions>? configure = null)
    {
        services.AddCkMethodIdempotencyOptions(configure);
        services.AddOptions<CkMethodDispatcherOptions>().BindConfiguration(CkMethodDispatcherOptions.SectionName);
        services.TryAddSingleton<ICkMethodPrincipalProvider, CkMethodPrincipalScope>();
        if (services.All(d => d.ImplementationType != typeof(CkMethodIdempotencyFilter)))
        {
            services.AddCkMethodInvocationFilter<CkMethodIdempotencyFilter>();
        }

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
