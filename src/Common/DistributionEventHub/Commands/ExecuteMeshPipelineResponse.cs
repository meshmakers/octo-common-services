namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Response after executing a mesh pipeline
/// </summary>
/// <param name="IsSuccessStartingExecution">Indicates if the execution STARTED successful</param>
/// <param name="ErrorMessage">An error message if the execution start failed</param>
/// <param name="PipelineExecutionId">The id of the pipeline execution</param>
/// <param name="ExecutionStartTime">Start time of the execution</param>
public record ExecuteMeshPipelineResponse(bool IsSuccessStartingExecution, string? ErrorMessage, Guid? PipelineExecutionId, DateTime? ExecutionStartTime);
