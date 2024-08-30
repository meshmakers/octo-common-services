using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Represents a pool
/// </summary>
public record PoolDescription
{
    /// <summary>
    /// Returns the name of the pool
    /// </summary>
    public string PoolName { get; set; } = null!;
    
    /// <summary>
    /// Returns the runtime id of the pool
    /// </summary>
    public OctoObjectId PoolRtId { get; set; }

    /// <summary>
    /// Returns the connection id of the pool
    /// </summary>
    public string? ConnectionId { get; set; }
}