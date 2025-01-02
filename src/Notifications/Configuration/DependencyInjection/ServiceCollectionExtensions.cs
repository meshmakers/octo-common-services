using Meshmakers.Octo.Services.Notifications;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoNotification(
        this IServiceCollection services)
    {
        services.AddCkModelSystemNotification();
        services.AddSingleton<IEventRepository, EntityEventRepository>();

        return services;
    }
}