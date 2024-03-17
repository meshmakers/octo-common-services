using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Event to trigger a pipeline by schedule
/// </summary>
/// <param name="TenantId">The tenant id</param>
/// <param name="PipelineRtIdList">The pipeline runtime id list</param>
public record PipelineTriggerSchedule(string TenantId, IEnumerable<OctoObjectId> PipelineRtIdList);