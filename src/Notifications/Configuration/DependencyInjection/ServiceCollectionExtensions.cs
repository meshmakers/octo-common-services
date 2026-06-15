using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddBlueprintSystemNotificationBootstrapV1();
        services.AddTransient<IEventRepository, EventRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IMarkdownRenderService, MarkdownRenderService>();

        // Replace the engine's default LoggingAuditEventSink with the bridge that persists
        // every audit event to the platform event repository (WI #3324 follow-up). All
        // typed engine audit-trail interfaces — ICkModelImportAuditTrail, IArchiveAuditTrail,
        // future additions — automatically route here because the engine's Forwarding*
        // defaults publish through IAuditEventSink. Per-interface bridges (such as the old
        // EventRepositoryCkModelImportAuditTrail) are no longer needed and have been
        // deleted; if a host wants to differentiate event sources per category it should
        // override IAuditEventSink rather than re-introduce per-interface bridges (which is
        // exactly what closed the WI #3324 DI bootstrap cycle).
        services.RemoveAll<IAuditEventSink>();
        services.AddSingleton<IAuditEventSink, EventRepositoryAuditEventSink>();

        return services;
    }
}