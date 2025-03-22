namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

/// <summary>
/// Event to trigger a pipeline by schedule
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param>
public record PipelineTriggerSchedule(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp) : EventBase(CorrelationId, Timestamp);