using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
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
        services.AddCkModelSystemNotificationV2();
        services.AddTransient<IEventRepository, EventRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IMarkdownRenderService, MarkdownRenderService>();

        // Replace the engine's default LoggingCkModelImportAuditTrail with the bridge that
        // writes import warnings (e.g. extensible-enum overrides — WI #3324 AC3) to the
        // platform event repository. Hosts that need a different RtEventSourcesEnum value
        // can re-register this service with a constructor argument after AddOctoNotification.
        services.AddTransient<ICkModelImportAuditTrail, EventRepositoryCkModelImportAuditTrail>();

        return services;
    }
}