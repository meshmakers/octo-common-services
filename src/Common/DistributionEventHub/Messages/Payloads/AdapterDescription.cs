using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of an adapter
/// </summary>
public record AdapterDescription
{
    /// <summary>
    /// Connection Id
    /// </summary>
    public string ConnectionId { get; }
    
    /// <summary>
    /// Adapter Runtime Id
    /// </summary>
    public OctoObjectId AdapterRtId { get; }
    
    /// <summary>
    /// Adapter configuration
    /// </summary>
    public AdapterConfigurationDto Configuration { get; }

    /// <summary>
    /// Creates a new instance of <see cref="AdapterDescription"/>
    /// </summary>
    /// <param name="adapterRtId"></param>
    /// <param name="connectionId"></param>
    /// <param name="configuration"></param>
    public AdapterDescription(OctoObjectId adapterRtId, string connectionId, AdapterConfigurationDto configuration)
    {
        AdapterRtId = adapterRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}