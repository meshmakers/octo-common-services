using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Requests the export of an Runtime model by a query.
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
    public OctoObjectId QueryId { get; set; }
}