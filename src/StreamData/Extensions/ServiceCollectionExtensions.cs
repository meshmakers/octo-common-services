using Meshmakers.Octo.Services.Common.StreamData.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Common.StreamData.Extensions;

/// <summary>
/// Extension methods for the service collection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IServiceCollection AddStreamDataDatabase(this IServiceCollection services, Action<StreamDataConfiguration> configure)
    {
        services.Configure(configure);

        services.AddSingletonMultipleInterfaces<
                CrateDatabaseClient, 
                IStreamDataDatabaseClient,
                IStreamDataDatabaseManagementClient>();
        
        return services;
    }
}