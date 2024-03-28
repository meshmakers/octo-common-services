using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Services.Common.Timeseries;

/// <summary>
/// 
/// </summary>
public class TsEntityDto
{
    /// <summary>
    /// 
    /// </summary>
    public DateTime TimeStamp { get; set; }
    
    /// <summary>
    ///     Gets or sets the id of the entity
    /// </summary>
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Gets or sets the type id of the entity
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the properties of the entity
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public IDictionary<string, object?>? Properties { get; set; }    
}