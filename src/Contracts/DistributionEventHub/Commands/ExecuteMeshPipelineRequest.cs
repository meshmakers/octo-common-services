namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Arguments for executing a mesh pipeline
/// </summary>
public record ExecuteMeshPipelineRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineInput">Optional pipeline input</param>
    public ExecuteMeshPipelineRequest(string tenantId, string? pipelineInput)
        : base(tenantId)
    {
        PipelineInput = pipelineInput;
    }

    /// <summary>
    /// An optional value as pipeline input
    /// </summary>
    public string? PipelineInput { get; init; }
}