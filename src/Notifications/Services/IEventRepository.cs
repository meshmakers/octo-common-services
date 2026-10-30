using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

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
    /// <param name="source">The source of the event</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreEventAsync(string tenantId, RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    ///     Stores an event in the repository.
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemEventAsync(RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtEntityId = null);


    /// <summary>
    ///     Stores an event with a category and structured details in the repository (AB#7821).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="category">Stable dotted category (for example <c>Method.Invocation</c>), or <c>null</c> for none.</param>
    /// <param name="details">Structured key/value details, or <c>null</c>/empty for none. Callers must not pass credentials or parameter values.</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreEventAsync(string tenantId, RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        string? category, IReadOnlyCollection<KeyValuePair<string, string>>? details,
        RtEntityId? associatedRtEntityId = null);

    /// <summary>
    ///     Stores an event with a category and structured details in the system tenant's repository (AB#7821).
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="category">Stable dotted category, or <c>null</c> for none.</param>
    /// <param name="details">Structured key/value details, or <c>null</c>/empty for none. Callers must not pass credentials or parameter values.</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemEventAsync(RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        string? category, IReadOnlyCollection<KeyValuePair<string, string>>? details,
        RtEntityId? associatedRtEntityId = null);

    /// <summary>
    ///     Stores a stateful event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="eventLevel">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns>The stored stateful event</returns>
    Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores an information event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreInformationEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores a warning event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreWarningEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores an error event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreErrorEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores a critical event in the repository.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreCriticalEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores an information event in the repository of the system tenant.
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemInformationEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores a warning event in the repository of the system tenant.
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemWarningEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
    
    /// <summary>
    /// Stores an error event in the repository of the system tenant.
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemErrorEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);

    /// <summary>
    /// Stores a critical event in the repository of the system tenant.
    /// </summary>
    /// <param name="source">The source of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task StoreSystemCriticalEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null);
}