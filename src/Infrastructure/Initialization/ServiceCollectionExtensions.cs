using Meshmakers.Octo.Backend.Infrastructure.Initialization;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddInitializationService<TService>(this IServiceCollection services)
        where TService : class, IAsyncInitializationService
    {
        services.AddTransient<IAsyncInitializationService, TService>();
        services.AddHostedService<HostedInitializer>();
    }
}