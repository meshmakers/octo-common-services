using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;

/// <summary>
///     Export runtime data arguments
/// </summary>
public record ExportRtCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="queryId"></param>
    public ExportRtCommandRequest(string tenantId, OctoObjectId queryId)
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