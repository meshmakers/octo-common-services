namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

/// <summary>
/// Command to execute a pipeline with a value
/// </summary>
/// <param name="TenantId">Corresponding tenant id</param>
/// <param name="CorrelationId">Correlates the event with other events</param>
/// <param name="Timestamp">Timestamp the event is created</param> 
/// <param name="PipelineRtId">RtId of pipeline to execute</param>
/// <param name="Value">Input value for the pipeline</param>
/// <param name="TransactionStartedDateTime">Timestamp the transaction started</param> 
public record ExecutePipeline(
    string TenantId,
    Guid CorrelationId,
    DateTime Timestamp,
    string PipelineRtId,
    string? Value,
    DateTime TransactionStartedDateTime) : EventBase(CorrelationId, Timestamp);