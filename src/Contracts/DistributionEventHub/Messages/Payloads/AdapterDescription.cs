using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of an adapter
/// </summary>
public record AdapterDescription
{
    /// <summary>
    /// Connection Id
    /// </summary>
    public string? ConnectionId { get; }
    
    /// <summary>
    /// Adapter Runtime Id
    /// </summary>
    public RtEntityId AdapterRtEntityId { get; }
    
    /// <summary>
    /// Adapter configuration
    /// </summary>
    public AdapterConfigurationDto Configuration { get; }

    /// <summary>
    /// Creates a new instance of <see cref="AdapterDescription"/>
    /// </summary>
    /// <param name="adapterRtEntityId"></param>
    /// <param name="connectionId"></param>
    /// <param name="configuration"></param>
    public AdapterDescription(RtEntityId adapterRtEntityId, string? connectionId, AdapterConfigurationDto configuration)
    {
        AdapterRtEntityId = adapterRtEntityId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}