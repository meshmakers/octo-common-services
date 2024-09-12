namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Response after executing a mesh pipeline
/// </summary>
public record ExecuteMeshPipelineResponse(bool IsSuccess, string? ErrorMessage, string? PipelineOutput);
