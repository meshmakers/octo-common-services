using System.Globalization;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Meshmakers.Octo.Services.Notifications;
using Xunit;

namespace Notifications.Tests;

public class EventRepositoryAuditEventSinkTests
{
    [Fact]
    public async Task PublishAsync_TenantEvent_PassesCategoryAndDetailsToTheRepository()
    {
        var repository = A.Fake<IEventRepository>();
        var sink = CreateSink(repository);

        await sink.PublishAsync(new AuditEvent("tenantA", AuditEventLevel.Error, "Method.Invocation", "failed")
        {
            Metadata = new Dictionary<string, object?> { ["method"] = "Basic/Ping", ["outcome"] = "Failed" },
        }, TestContext.Current.CancellationToken);

        A.CallTo(() => repository.StoreEventAsync("tenantA", RtEventSourcesEnum.AssetRepositoryService,
                RtEventLevelsEnum.Error, "failed", "Method.Invocation",
                A<IReadOnlyCollection<KeyValuePair<string, string>>?>.That.Matches(d =>
                    d != null && d.Count == 2 && d.Contains(new KeyValuePair<string, string>("method", "Basic/Ping"))),
                null))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PublishAsync_SystemEvent_UsesTheSystemOverload()
    {
        var repository = A.Fake<IEventRepository>();
        var sink = CreateSink(repository);

        await sink.PublishAsync(new AuditEvent(null, AuditEventLevel.Information, "Archive.Transition", "ok"),
            TestContext.Current.CancellationToken);

        A.CallTo(() => repository.StoreSystemEventAsync(RtEventSourcesEnum.AssetRepositoryService,
                RtEventLevelsEnum.Information, "ok", "Archive.Transition", null, null))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PublishAsync_WhenTheRepositoryFails_LogsAndDoesNotThrow()
    {
        var repository = A.Fake<IEventRepository>();
        A.CallTo(() => repository.StoreEventAsync(A<string>._, A<RtEventSourcesEnum>._, A<RtEventLevelsEnum>._,
                A<string>._, A<string?>._, A<IReadOnlyCollection<KeyValuePair<string, string>>?>._, A<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>._))
            .Throws(new EventStoreFailedException("boom", new InvalidOperationException()));
        var sink = CreateSink(repository);

        var exception = await Record.ExceptionAsync(() => sink.PublishAsync(
            new AuditEvent("tenantA", AuditEventLevel.Warning, "Method.Invocation", "m"),
            TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public void MapMetadata_SkipsNullValues_AndRendersWithTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-AT");
        try
        {
            var details = EventRepositoryAuditEventSink.MapMetadata(new Dictionary<string, object?>
            {
                ["number"] = 1234.5,
                ["when"] = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc),
                ["flag"] = true,
                ["level"] = AuditEventLevel.Warning,
                ["text"] = "plain",
                ["list"] = new List<string> { "a", "b" },
                ["nothing"] = null,
            });

            Assert.NotNull(details);
            var map = details!.ToDictionary(d => d.Key, d => d.Value);
            Assert.Equal("1234.5", map["number"]);
            Assert.Equal("2026-10-30T08:00:00.0000000Z", map["when"]);
            Assert.Equal("True", map["flag"]);
            Assert.Equal("Warning", map["level"]);
            Assert.Equal("plain", map["text"]);
            Assert.Equal("a, b", map["list"]);
            Assert.False(map.ContainsKey("nothing"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void MapMetadata_NullOrAllNull_ReturnsNull()
    {
        Assert.Null(EventRepositoryAuditEventSink.MapMetadata(null));
        Assert.Null(EventRepositoryAuditEventSink.MapMetadata(new Dictionary<string, object?>()));
        Assert.Null(EventRepositoryAuditEventSink.MapMetadata(new Dictionary<string, object?> { ["a"] = null }));
    }

    private static EventRepositoryAuditEventSink CreateSink(IEventRepository repository)
    {
        var services = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        return new EventRepositoryAuditEventSink(services, Options.Create(new AuditEventSinkOptions()),
            NullLogger<EventRepositoryAuditEventSink>.Instance);
    }
}
