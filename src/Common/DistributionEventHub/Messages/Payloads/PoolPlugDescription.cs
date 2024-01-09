using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of a plug in a pool
/// </summary>
public record PoolPlugDescription
{
    /// <summary>
    /// Plug runtime id
    /// </summary>
    public OctoObjectId PlugRtId { get; set; }
    
    /// <summary>
    /// Pool runtime id
    /// </summary>
    public OctoObjectId PoolRtId { get; set; }
    
    /// <summary>
    /// The corresponding adapter
    /// </summary>
    public PoolCommunicationAdapterDto AdapterDto { get; set; } = null!;
}