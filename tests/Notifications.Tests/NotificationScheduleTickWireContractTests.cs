using System.Text.Json;
using MassTransit.Serialization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Xunit;

namespace Notifications.Tests;

public class NotificationScheduleTickWireContractTests
{
    [Fact]
    public void RoundTrip_UnderTheHubSerializer_KeepsEveryField()
    {
        var tick = new NotificationScheduleTick("ecfinal", "6501f2a3b4c5d6e7f8a9b0c1", Guid.NewGuid(),
            new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(tick, SystemTextJsonMessageSerializer.Options);
        var back = JsonSerializer.Deserialize<NotificationScheduleTick>(json, SystemTextJsonMessageSerializer.Options);

        Assert.Equal(tick, back);
    }

    [Fact]
    public void Serialized_UsesTheFieldNamesTheConsumerReads()
    {
        var json = JsonSerializer.SerializeToElement(
            new NotificationScheduleTick("ecfinal", "pref", Guid.Empty, DateTime.UnixEpoch),
            SystemTextJsonMessageSerializer.Options);

        Assert.Equal("ecfinal", json.GetProperty("tenantId").GetString());
        Assert.Equal("pref", json.GetProperty("preferenceRtId").GetString());
    }
}
