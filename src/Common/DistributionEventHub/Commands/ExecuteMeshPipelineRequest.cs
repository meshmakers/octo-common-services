using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Arguments for executing a mesh pipeline
/// </summary>
public record ExecuteMeshPipelineRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="meshPipelineRtEntityId">Mesh pipeline runtime entity id</param>
    /// <param name="pipelineInput">Optional pipeline input</param>
    public ExecuteMeshPipelineRequest(string tenantId, RtEntityId meshPipelineRtEntityId, string? pipelineInput)
        : base(tenantId)
    {
        MeshPipelineRtEntityId = meshPipelineRtEntityId;
        PipelineInput = pipelineInput;
    }

    /// <summary>
    /// Mesh pipeline runtime entity id
    /// </summary>
    public RtEntityId MeshPipelineRtEntityId { get; init; }

    /// <summary>
    /// An optional value as pipeline input
    /// </summary>
    public string? PipelineInput { get; init; }
}