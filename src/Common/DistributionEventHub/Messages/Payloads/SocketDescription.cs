using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

/// <summary>
/// Description of a socket.
/// </summary>
public record SocketDescription
{
    /// <summary>
    /// Connection id of the socket.
    /// </summary>
    public string ConnectionId { get; }
    
    /// <summary>
    /// Socket runtime id.
    /// </summary>
    public OctoObjectId SocketRtId { get; }
    
    /// <summary>
    /// Socket configuration.
    /// </summary>
    public SocketConfigurationDto Configuration { get; }
    

    /// <summary>
    /// Initializes a new instance of the <see cref="SocketDescription"/> class.
    /// </summary>
    /// <param name="socketRtId"></param>
    /// <param name="connectionId"></param>
    /// <param name="configuration"></param>
    public SocketDescription(OctoObjectId socketRtId, string connectionId, SocketConfigurationDto configuration)
    {
        SocketRtId = socketRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}