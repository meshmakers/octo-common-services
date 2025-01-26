using Meshmakers.Octo.Services.Notifications.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoNotification(
        this IServiceCollection services)
    {
        services.AddCkModelSystemNotification();
        services.AddTransient<IEventRepository, EventRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IMarkdownRenderService, MarkdownRenderService>();

        return services;
    }
}