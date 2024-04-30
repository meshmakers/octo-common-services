using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Services.Common.StreamData.Dtos;

/// <summary>
/// Represents a data point.
/// </summary>
public class DataPointDto : RtTypeWithAttributes
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="attributes"></param>
    public DataPointDto(Dictionary<string, object?> attributes) : base(attributes)
    {
        
    }
    
    /// <summary>
    /// The id of the entity that the datapoint is associated with.
    /// </summary>
    public OctoObjectId? RtId { get; set; }
    
    /// <summary>
    /// The type id of the entity that the datapoint is associated with.
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }
    
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
    public OctoObjectId PlugId { get; set; }
    
    /// <summary>
    /// the external id of the data point
    /// </summary>
    public OctoObjectId ExternalId { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    protected override string GetLocation()
    {
        return "StreamData";
    }
}