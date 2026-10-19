using System.Text.Json;
using MassTransit.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Xunit;

namespace Notifications.Tests;

/// <summary>
///     Pins the wire contract of the two export requests for the optional, additive caller members
///     (AB#6734): the initiating caller (subject, roles, client) travels with the message, and a message of an
///     older producer (no such members) still deserializes, with no caller, so that the consumer can refuse it.
/// </summary>
public class ExportRtCommandRequestWireContractTests
{
    private static readonly JsonSerializerOptions Options = SystemTextJsonMessageSerializer.Options;

    [Fact]
    public void ExportByQuery_CallerRoundTrips()
    {
        var queryId = OctoObjectId.GenerateNewId();
        var request = new ExportRtByQueryCommandRequest("tenant-1", queryId)
        {
            InitiatedBySubjectId = "user-sub-1",
            InitiatedByRoleNames = ["TenantAdmin", "Reader"],
            InitiatedByClientId = "studio"
        };

        var back = JsonSerializer.Deserialize<ExportRtByQueryCommandRequest>(
            JsonSerializer.Serialize(request, Options), Options);

        Assert.NotNull(back);
        Assert.Equal(queryId, back.QueryId);
        Assert.Equal("user-sub-1", back.InitiatedBySubjectId);
        Assert.Equal(["TenantAdmin", "Reader"], back.InitiatedByRoleNames);
        Assert.Equal("studio", back.InitiatedByClientId);
    }

    [Fact]
    public void ExportByDeepGraph_CallerRoundTrips()
    {
        var origin = OctoObjectId.GenerateNewId();
        var request = new ExportRtByDeepGraphCommandRequest("tenant-1", [origin], new RtCkId<CkTypeId>("Test/Customer"))
        {
            InitiatedBySubjectId = "svc-client",
            InitiatedByRoleNames = [],
            InitiatedByClientId = "svc-client"
        };

        var back = JsonSerializer.Deserialize<ExportRtByDeepGraphCommandRequest>(
            JsonSerializer.Serialize(request, Options), Options);

        Assert.NotNull(back);
        Assert.Equal([origin], back.OriginRtIds);
        Assert.Equal("svc-client", back.InitiatedBySubjectId);
        Assert.Empty(back.InitiatedByRoleNames!);
        Assert.Equal("svc-client", back.InitiatedByClientId);
    }

    [Fact]
    public void MessageOfAnOlderProducer_DeserializesWithoutACaller()
    {
        var older = JsonSerializer.Serialize(
            new PreviousExportByQueryRequest("tenant-1", OctoObjectId.GenerateNewId()), Options);

        var back = JsonSerializer.Deserialize<ExportRtByQueryCommandRequest>(older, Options);

        Assert.NotNull(back);
        Assert.Equal("tenant-1", back.TenantId);
        Assert.Null(back.InitiatedBySubjectId);
        Assert.Null(back.InitiatedByRoleNames);
        Assert.Null(back.InitiatedByClientId);
    }

    [Fact]
    public void AnOlderConsumer_IgnoresTheNewMembers()
    {
        var queryId = OctoObjectId.GenerateNewId();
        var json = JsonSerializer.Serialize(new ExportRtByQueryCommandRequest("tenant-1", queryId)
        {
            InitiatedBySubjectId = "user-sub-1",
            InitiatedByRoleNames = ["Reader"],
            InitiatedByClientId = "studio"
        }, Options);

        var previous = JsonSerializer.Deserialize<PreviousExportByQueryRequest>(json, Options);

        Assert.Equal(new PreviousExportByQueryRequest("tenant-1", queryId), previous);
    }

    // The contract as an older consumer compiled it: no caller members.
    private sealed record PreviousExportByQueryRequest(string TenantId, OctoObjectId QueryId);
}
