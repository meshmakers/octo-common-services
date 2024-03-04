namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Command to execute a pipeline with a value
/// </summary>
public record ExecutePipeline(string TenantId, string PipelineRtId, string? Value, DateTime TransactionStartedDateTime);
