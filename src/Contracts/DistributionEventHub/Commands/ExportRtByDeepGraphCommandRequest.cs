using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

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
        RtCkId<CkTypeId> originCkTypeId)
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
    [JsonConverter(typeof(RtCkIdTypeIdConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(NewtonRtCkTypeIdConverter))]
    public RtCkId<CkTypeId> OriginCkTypeId { get; set; }

    /// <summary>
    ///     Optional directed follow rules the deep-graph traversal applies (AB#5003). Null or empty
    ///     keeps the default ParentChild traversal; consumers on older versions ignore the field.
    /// </summary>
    public IEnumerable<DeepGraphFollowSpecRequest>? FollowSpecs { get; set; }
}

/// <summary>
///     One directed edge-following rule (AB#5003): follow the association role
///     <see cref="RoleId" /> only in <see cref="Direction" />.
/// </summary>
public record DeepGraphFollowSpecRequest(string RoleId, Meshmakers.Octo.ConstructionKit.Contracts.GraphDirections Direction);