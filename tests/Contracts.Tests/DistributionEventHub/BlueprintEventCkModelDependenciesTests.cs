using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Xunit;

namespace Contracts.Tests.DistributionEventHub;

/// <summary>
///     The optional CkModelDependencies of the blueprint events is additive on the wire (System.Text.Json, as the event
///     hub transport): it round-trips, a message of an older publisher (no property) reads as null, and an older
///     consumer ignores it.
/// </summary>
public class BlueprintEventCkModelDependenciesTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Applied_RoundTripsTheDependencies()
    {
        var sent = new BlueprintApplied("tenant-a", "Starter-1.0.0", "Initial", 3, 0, 0, Guid.NewGuid(),
            DateTime.UtcNow) { CkModelDependencies = ["System.Ai", "System.Communication"] };

        var received = JsonSerializer.Deserialize<BlueprintApplied>(JsonSerializer.Serialize(sent, Web), Web);

        Assert.NotNull(received);
        Assert.Equal(["System.Ai", "System.Communication"], received.CkModelDependencies);
        Assert.Equal(sent.TenantId, received.TenantId);
    }

    [Fact]
    public void Updated_RoundTripsTheDependencies()
    {
        var sent = new BlueprintUpdated("tenant-a", "Starter-2.0.0", "Starter-1.0.0", "Merge", 0, 1, 0,
            Guid.NewGuid(), DateTime.UtcNow) { CkModelDependencies = ["System.Communication"] };

        var received = JsonSerializer.Deserialize<BlueprintUpdated>(JsonSerializer.Serialize(sent, Web), Web);

        Assert.NotNull(received);
        Assert.Equal(["System.Communication"], received.CkModelDependencies);
    }

    [Fact]
    public void MessageOfAnOlderPublisher_ReadsAsNull()
    {
        const string olderApplied = """
            {"tenantId":"tenant-a","blueprintId":"Starter-1.0.0","applicationMode":"Initial","entitiesAdded":1,
             "entitiesUpdated":0,"entitiesDeleted":0,"correlationId":"6f1c2d38-0f43-4b6a-9b65-3b2f4e1d2a10",
             "timestamp":"2026-11-22T20:00:00Z"}
            """;
        const string olderUpdated = """
            {"tenantId":"tenant-a","blueprintId":"Starter-2.0.0","fromVersion":null,"updateMode":"Merge",
             "entitiesAdded":0,"entitiesUpdated":1,"entitiesDeleted":0,
             "correlationId":"6f1c2d38-0f43-4b6a-9b65-3b2f4e1d2a10","timestamp":"2026-11-22T20:00:00Z"}
            """;

        Assert.Null(JsonSerializer.Deserialize<BlueprintApplied>(olderApplied, Web)!.CkModelDependencies);
        Assert.Null(JsonSerializer.Deserialize<BlueprintUpdated>(olderUpdated, Web)!.CkModelDependencies);
    }

    [Fact]
    public void OlderConsumer_IgnoresTheNewProperty()
    {
        var sent = new BlueprintApplied("tenant-a", "Starter-1.0.0", "Initial", 3, 0, 0, Guid.NewGuid(),
            DateTime.UtcNow) { CkModelDependencies = ["System.Ai"] };

        var older = JsonSerializer.Deserialize<OlderBlueprintApplied>(JsonSerializer.Serialize(sent, Web), Web);

        Assert.NotNull(older);
        Assert.Equal("Starter-1.0.0", older.BlueprintId);
    }

    /// <summary>The event as a consumer built before the property knows it.</summary>
    private sealed record OlderBlueprintApplied(
        string TenantId,
        string BlueprintId,
        string ApplicationMode,
        int EntitiesAdded,
        int EntitiesUpdated,
        int EntitiesDeleted,
        Guid CorrelationId,
        DateTime Timestamp);
}
