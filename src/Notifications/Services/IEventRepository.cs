using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Services.Notifications.Services;

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
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtId = null);


    /// <summary>
    ///     Stores a stateful event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtId">Optional entity identifier the notification event is associated to.</param>
    /// <returns>The stored stateful event</returns>
    Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtId = null);
}