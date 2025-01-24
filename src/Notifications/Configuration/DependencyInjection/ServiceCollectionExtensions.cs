using Meshmakers.Octo.Services.Notifications;
using Meshmakers.Octo.Services.Notifications.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoNotification(
        this IServiceCollection services)
    {
        services.AddCkModelSystemNotification();
        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<INotificationService, NotificationService>();

        return services;
    }
}