using Meshmakers.Octo.Services.Methods.MongoDb;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the MongoDB operation store (AB#8443).</summary>
public static class CkMethodMongoOperationStoreServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="CkMethodMongoOperationStore" /> (collection <c>MethodOperations</c> in the system database)
    ///     as <see cref="ICkMethodOperationStore" /> unless a store is registered, and binds <c>Methods:Operations</c>. The
    ///     host already configures <c>OctoSystemConfiguration</c> (section <c>System</c>) for the engine; the store needs its
    ///     admin credentials. Combine with <c>AddCkMethodOperations()</c> (handler hosts) or <c>AddCkMethodOperationSweep()</c>
    ///     (readers). AB#8447: also registers <see cref="ICkMethodOperationTenantPurge" /> (the store's purge of a deleted
    ///     tenant).
    /// </summary>
    public static IServiceCollection AddCkMethodMongoOperationStore(this IServiceCollection services,
        Action<CkMethodOperationOptions>? configure = null)
    {
        services.AddCkMethodOperationOptions(configure);
        services.TryAddSingleton<ICkMethodOperationStore, CkMethodMongoOperationStore>();
        services.AddCkMethodOperationTenantPurge();
        return services;
    }
}
