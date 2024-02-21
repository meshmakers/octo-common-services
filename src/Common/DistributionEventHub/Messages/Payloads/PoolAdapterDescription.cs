using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of an adapter in a pool
/// </summary>
public record PoolAdapterDescription
{
    /// <summary>
    /// Adapter runtime id
    /// </summary>
    public OctoObjectId AdapterRtId { get; set; }
    
    /// <summary>
    /// Pool runtime id
    /// </summary>
    public OctoObjectId PoolRtId { get; set; }
    
    /// <summary>
    /// The corresponding adapter
    /// </summary>
    public PoolCommunicationAdapterDto AdapterDto { get; set; } = null!;
}