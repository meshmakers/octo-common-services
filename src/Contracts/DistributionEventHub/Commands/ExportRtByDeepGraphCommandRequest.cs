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

    /// <summary>
    ///     Subject id of the caller that started the export through the API (AB#6734): the user <c>sub</c>
    ///     claim or, for a client-credentials token without <c>sub</c>, the client id (the same mapping the
    ///     GraphQL surface uses). The export job opens its repository session with this identity, roles and
    ///     client, so the data permissions of the caller apply. Optional and additive on the wire: a message of
    ///     an older producer carries none, and the consumer then refuses the job (fail closed, never an unfiltered
    ///     export); an older consumer ignores the field.
    /// </summary>
    public string? InitiatedBySubjectId { get; init; }

    /// <summary>
    ///     Role names of the caller as issued in its token's role claims (AB#6734); input for the data
    ///     permissions of the export. Optional and additive.
    /// </summary>
    public IReadOnlyList<string>? InitiatedByRoleNames { get; init; }

    /// <summary>
    ///     Client id (<c>client_id</c> claim) of the caller's token (AB#6734), for audit and diagnostics.
    ///     Optional and additive.
    /// </summary>
    public string? InitiatedByClientId { get; init; }
}

/// <summary>
///     One directed edge-following rule (AB#5003): follow the association role
///     <see cref="RoleId" /> only in <see cref="Direction" />.
/// </summary>
public record DeepGraphFollowSpecRequest(string RoleId, Meshmakers.Octo.ConstructionKit.Contracts.GraphDirections Direction);