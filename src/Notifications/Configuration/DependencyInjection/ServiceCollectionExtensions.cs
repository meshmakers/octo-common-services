using Meshmakers.Octo.Services.Notifications.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extensions for dependency injection's service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Notification components to the service collection
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddOctoNotification(
        this IServiceCollection services)
    {
        services.AddCkModelSystemNotificationV1();
        services.AddTransient<IEventRepository, EventRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IMarkdownRenderService, MarkdownRenderService>();

        return services;
    }
}