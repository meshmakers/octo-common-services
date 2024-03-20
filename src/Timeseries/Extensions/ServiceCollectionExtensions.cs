using Meshmakers.Octo.Services.Common.Timeseries.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Common.Timeseries.Extensions;

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
    public static IServiceCollection AddTimeSeriesDatabase(this IServiceCollection services, Action<TimeSeriesConfiguration> configure)
    {
        services.Configure(configure);

        services.AddSingletonMultipleInterfaces<
                CrateDatabaseClient, 
                ITimeSeriesDatabaseClient,
                ITimeSeriesDatabaseManagementClient>();
        
        return services;
    }
}