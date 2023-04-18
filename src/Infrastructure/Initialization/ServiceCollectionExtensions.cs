using Meshmakers.Octo.Backend.Infrastructure.Initialization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     flag to make sure to only add one instance of the hosted service to the DI collection
    /// </summary>
    private static bool _initialized;

    public static void AddInitializationService<TService>(this IServiceCollection services)
        where TService : class, IAsyncInitializationService
    {
        services.AddTransient<IAsyncInitializationService, TService>();
        if (!_initialized)
        {
            services.AddHostedService<HostedInitializer>();
            _initialized = true;
        }
    }
}
