using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of a plug
/// </summary>
public record PlugDescription
{
    /// <summary>
    /// Connection Id
    /// </summary>
    public string ConnectionId { get; }
    
    /// <summary>
    /// Plug Runtime Id
    /// </summary>
    public OctoObjectId PlugRtId { get; }
    
    /// <summary>
    /// Plug configuration
    /// </summary>
    public PlugConfigurationDto Configuration { get; }
    

    /// <summary>
    /// Creates a new instance of <see cref="PlugDescription"/>
    /// </summary>
    /// <param name="plugRtId"></param>
    /// <param name="connectionId"></param>
    /// <param name="configuration"></param>
    public PlugDescription(OctoObjectId plugRtId, string connectionId, PlugConfigurationDto configuration)
    {
        PlugRtId = plugRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}