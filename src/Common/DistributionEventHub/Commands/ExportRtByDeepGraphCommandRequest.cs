using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Requests the export of a Runtime model by a query.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record ExportRtByDeepGraphCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="originRtIds">Origin runtime ids</param>
    /// <param name="originCkTypeId">Origin construction kit type id</param>
    public ExportRtByDeepGraphCommandRequest(string tenantId, IEnumerable<OctoObjectId> originRtIds,
        CkId<CkTypeId> originCkTypeId)
        : base(tenantId)
    {
        OriginRtIds = originRtIds;
        OriginCkTypeId = originCkTypeId;
    }

    /// <summary>
    ///     The RtIds as starting point of the deep graph export
    /// </summary>
    [JsonConverter(typeof(OctoObjectIdEnumerableConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(NewtonOctoObjectIdEnumerableConverter))]
    public IEnumerable<OctoObjectId> OriginRtIds { get; set; }
    
    /// <summary>
    ///     The CkTypeId as starting point of the deep graph export
    /// </summary>
    [JsonConverter(typeof(CkIdTypeIdConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(NewtonCkTypeIdConverter))]
    public CkId<CkTypeId> OriginCkTypeId { get; set; }
}