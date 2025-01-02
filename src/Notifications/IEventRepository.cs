using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Services.Notifications;

/// <summary>
///     Interface for the event repository.
/// </summary>
public interface IEventRepository
{
    /// <summary>
    ///     Stores an event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="body">The body of the event</param>
    /// <returns></returns>
    Task StoreEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string subject, string? body);

    /// <summary>
    ///     Stores an event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="body">The body of the event</param>
    /// <param name="associatedRtId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string subject, string? body,
        RtEntityId? associatedRtId);

    /// <summary>
    ///     Stores a stateful event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="body">The body of the event</param>
    /// <returns>The stored stateful event</returns>
    Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string subject,
        string? body);

    /// <summary>
    ///     Stores a stateful event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="body">The body of the event</param>
    /// <param name="associatedRtId">Optional entity identifier the notification event is associated to.</param>
    /// <returns>The stored stateful event</returns>
    Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string subject,
        string? body, RtEntityId? associatedRtId);
}