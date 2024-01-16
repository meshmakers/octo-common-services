using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Common;

/// <summary>
/// This class provides extension methods for <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    
    /// <summary>
    /// This method adds a singleton instance of <typeparamref name="TImplementation"/> to the service collection.
    /// If services require <typeparamref name="TInterface1"/> or <typeparamref name="TInterface2"/>, they will get the same instance.
    /// </summary>
    /// <param name="serviceCollection"></param>
    /// <typeparam name="TImplementation"></typeparam>
    /// <typeparam name="TInterface1"></typeparam>
    /// <typeparam name="TInterface2"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddSingletonMultipleInterfaces<TImplementation, TInterface1, TInterface2>(
        this IServiceCollection serviceCollection)
        where TImplementation : class, TInterface1, TInterface2
        where TInterface1 : class
        where TInterface2 : class
    {
        serviceCollection.AddSingleton<TImplementation>();
        serviceCollection.AddSingleton<TInterface1>(p => p.GetRequiredService<TImplementation>());
        serviceCollection.AddSingleton<TInterface2>(p => p.GetRequiredService<TImplementation>());
        return serviceCollection;
    }

    /// <summary>
    /// This method adds a singleton instance of <typeparamref name="TImplementation"/> to the service collection.
    /// If services require <typeparamref name="TInterface1"/> or <typeparamref name="TInterface2"/> or <typeparamref name="TInterface3"/> or  they will get the same instance.
    /// </summary>
    /// <param name="serviceCollection"></param>
    /// <typeparam name="TImplementation"></typeparam>
    /// <typeparam name="TInterface1"></typeparam>
    /// <typeparam name="TInterface2"></typeparam>
    /// <typeparam name="TInterface3"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddSingletonMultipleInterfaces<TImplementation, TInterface1, TInterface2, TInterface3>(
        this IServiceCollection serviceCollection)
        where TImplementation : class, TInterface1, TInterface2, TInterface3
        where TInterface1 : class
        where TInterface2 : class
        where TInterface3 : class
    {
        serviceCollection.AddSingletonMultipleInterfaces<TImplementation, TInterface1, TInterface2>();
        serviceCollection.AddSingleton<TInterface3>(p => p.GetRequiredService<TImplementation>());
        return serviceCollection;
    }
}