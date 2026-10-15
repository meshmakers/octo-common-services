namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
/// A tick of one notification preference's schedule: the notification pipeline runs the preference's
/// subscription query and mails its user the rows that are new since their previous run.
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="PreferenceRtId">The notification preference whose schedule fired</param>
/// <param name="CorrelationId">Set once when the schedule is created; every tick of the schedule carries the same value, so it does not identify a run</param>
/// <param name="Timestamp">When the schedule was created, not when the tick fired; identical on every tick</param>
public record NotificationScheduleTick(
    string TenantId,
    string PreferenceRtId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);
