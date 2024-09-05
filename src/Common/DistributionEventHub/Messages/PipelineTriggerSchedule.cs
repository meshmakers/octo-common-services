using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Event to trigger a pipeline by schedule
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param>
/// <param name="PipelineRtIdList">The pipeline runtime id list</param>
public record PipelineTriggerSchedule(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    ICollection<OctoObjectId> PipelineRtIdList) : EventBase(CorrelationId, Timestamp);