using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.StreamData.Dapper;

namespace Meshmakers.Octo.Services.StreamData;


/// <summary>
/// A data point in the stream data database.
/// </summary>
internal class DapperSerializableDatapoint
{
    /// <summary>
    /// The id of the entity that the datapoint is associated with.
    /// </summary>
    public required RtEntityId DataRtId { get; set; }
    
    /// <summary>
    /// The timestamp in UTC.
    /// </summary>
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// when was the datapoint received by the plug
    /// </summary>
    public DateTime AdapterReceivedTimestamp { get; set; }
    
    /// <summary>
    /// The id of the plug that received the data point
    /// </summary>
    public required OctoObjectId PlugId { get; set; }
    
    /// <summary>
    /// the external id of the data point
    /// </summary>
    public required OctoObjectId ExternalId { get; set; }

    /// <summary>
    /// The value of the datapoint.
    /// </summary>
    public Json<Dictionary<string, object?>> Data { get; set; } = null!;
}