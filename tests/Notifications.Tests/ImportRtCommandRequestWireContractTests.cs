using System.Text.Json;
using MassTransit.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Xunit;

namespace Notifications.Tests;

/// <summary>
///     Pins the wire contract of <see cref="ImportRtCommandRequest" /> for the optional, additive
///     <see cref="ImportRtCommandRequest.InitiatedBySubjectId" /> (AB#6392): the initiating caller travels with the
///     message, and a message of an older producer (no such field) still deserializes, with a null caller.
/// </summary>
public class ImportRtCommandRequestWireContractTests
{
    private static readonly JsonSerializerOptions Options = SystemTextJsonMessageSerializer.Options;

    [Fact]
    public void InitiatedBySubjectId_RoundTrips()
    {
        var request = new ImportRtCommandRequest("tenant-1", ImportStrategy.Upsert, "cache-key")
        {
            InitiatedBySubjectId = "user-sub-1"
        };

        var json = JsonSerializer.Serialize(request, Options);
        var back = JsonSerializer.Deserialize<ImportRtCommandRequest>(json, Options);

        Assert.NotNull(back);
        Assert.Equal("tenant-1", back.TenantId);
        Assert.Equal(ImportStrategy.Upsert, back.ImportStrategy);
        Assert.Equal("cache-key", back.CacheFileKey);
        Assert.Equal("user-sub-1", back.InitiatedBySubjectId);
    }

    [Fact]
    public void MessageOfAnOlderProducer_DeserializesWithoutACaller()
    {
        // exactly what the previous contract serialized: no initiatedBySubjectId member
        var older = JsonSerializer.Serialize(
            new PreviousImportRtCommandRequest("tenant-1", ImportStrategy.Insert, "cache-key"), Options);

        var back = JsonSerializer.Deserialize<ImportRtCommandRequest>(older, Options);

        Assert.NotNull(back);
        Assert.Equal("tenant-1", back.TenantId);
        Assert.Null(back.InitiatedBySubjectId);
    }

    // The contract as an older consumer compiled it: no InitiatedBySubjectId.
    private sealed record PreviousImportRtCommandRequest(string TenantId, ImportStrategy ImportStrategy,
        string CacheFileKey);

    [Fact]
    public void AnOlderConsumer_IgnoresTheNewField()
    {
        var request = new ImportRtCommandRequest("tenant-1", ImportStrategy.Upsert, "cache-key")
        {
            InitiatedBySubjectId = "user-sub-1"
        };
        var json = JsonSerializer.Serialize(request, Options);

        var previous = JsonSerializer.Deserialize<PreviousImportRtCommandRequest>(json, Options);

        Assert.Equal(new PreviousImportRtCommandRequest("tenant-1", ImportStrategy.Upsert, "cache-key"), previous);
    }
}
