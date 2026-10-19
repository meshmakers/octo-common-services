using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Serialization;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Requests the export of a Runtime model by a query.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record ExportRtByQueryCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">ID of query, whose data is exported</param>
    public ExportRtByQueryCommandRequest(string tenantId, OctoObjectId queryId)
        : base(tenantId)
    {
        QueryId = queryId;
    }

    /// <summary>
    ///     Query id to export
    /// </summary>
    [JsonConverter(typeof(OctoObjectIdConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId QueryId { get; set; }

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