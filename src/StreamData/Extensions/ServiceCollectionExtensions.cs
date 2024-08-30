using Meshmakers.Octo.Services.Common.StreamData.Client;
using Meshmakers.Octo.Services.Common.StreamData.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// <returns></returns>
    public static IServiceCollection AddStreamDataDatabase<TConfigureOptions>(this IServiceCollection services)
        where TConfigureOptions : IConfigureNamedOptions<StreamDataConfiguration>
    {
        services.ConfigureOptions(typeof(TConfigureOptions));

        services.AddSingletonMultipleInterfaces<
            CrateDatabaseClient,
            IStreamDataDatabaseClient,
            IStreamDataDatabaseManagementClient,
            IStreamDataHealthCheckClient>();

        services.AddSingleton<ICrateDbConnectionAccess, CrateDbConnectionAccess>();

        return services;
    }
}