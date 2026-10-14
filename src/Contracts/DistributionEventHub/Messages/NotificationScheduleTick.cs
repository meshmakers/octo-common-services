namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
/// A tick of one notification preference's schedule: the notification pipeline runs the preference's
/// subscription query and mails its user the rows that are new since their previous run.
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="PreferenceRtId">The notification preference whose schedule fired</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param>
public record NotificationScheduleTick(
    string TenantId,
    string PreferenceRtId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
