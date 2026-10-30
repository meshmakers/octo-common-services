using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.IntegrationTests;

/// <summary>
///     AB#7821: the audit sink persists an audit event's category and metadata as <c>Category</c> and
///     <c>Details</c> of the stored <c>Event</c>, for a tenant and for the system tenant, and events written
///     without them still round-trip. Runs against a real MongoDB.
/// </summary>
public class EventLogCategoryDetailsTests(NotificationsMongoFixture fixture) : IClassFixture<NotificationsMongoFixture>
{
    private IAuditEventSink Sink => fixture.Provider!.GetRequiredService<IAuditEventSink>();

    [Fact]
    public async Task Sink_TenantAuditEvent_StoresCategoryAndDetails()
    {
        fixture.SkipIfUnavailable();
        var category = $"Method.Invocation.{Guid.NewGuid():N}";

        await Sink.PublishAsync(AuditWith(NotificationsMongoFixture.ChildTenantId, category), TestContext.Current.CancellationToken);

        var stored = Assert.Single(await ReadAsync(NotificationsMongoFixture.ChildTenantId, category));
        AssertStored(stored, category);
        Assert.Equal(RtEventLevelsEnum.Warning, stored.Level);
    }

    [Fact]
    public async Task Sink_SystemAuditEvent_StoresCategoryAndDetails()
    {
        fixture.SkipIfUnavailable();
        var category = $"Method.Invocation.{Guid.NewGuid():N}";

        await Sink.PublishAsync(AuditWith(null, category), TestContext.Current.CancellationToken);

        var stored = Assert.Single(await ReadAsync(null, category));
        AssertStored(stored, category);
    }

    [Fact]
    public async Task Event_WrittenWithoutCategory_IsStillReadCorrectly()
    {
        fixture.SkipIfUnavailable();
        var message = $"legacy {Guid.NewGuid():N}";
        var repository = fixture.Provider!.GetRequiredService<IEventRepository>();

        // The callers that predate System.Notification 2.7.0 use these overloads.
        await repository.StoreInformationEvent(NotificationsMongoFixture.ChildTenantId,
            RtEventSourcesEnum.AssetRepositoryService, message);
        await repository.StoreSystemInformationEvent(RtEventSourcesEnum.AssetRepositoryService, message);

        foreach (var tenantId in new string?[] { NotificationsMongoFixture.ChildTenantId, null })
        {
            var stored = Assert.Single(await ReadByMessageAsync(tenantId, message));
            Assert.Null(stored.Category);
            Assert.True(stored.Details is null or { Count: 0 });
            Assert.Equal(RtEventLevelsEnum.Information, stored.Level);
        }
    }

    [Fact]
    public async Task Sink_AuditEventWithoutMetadata_StoresCategoryOnly()
    {
        fixture.SkipIfUnavailable();
        var category = $"Archive.Transition.{Guid.NewGuid():N}";

        await Sink.PublishAsync(
            new AuditEvent(NotificationsMongoFixture.ChildTenantId, AuditEventLevel.Information, category, "no metadata"),
            TestContext.Current.CancellationToken);

        var stored = Assert.Single(await ReadAsync(NotificationsMongoFixture.ChildTenantId, category));
        Assert.Equal(category, stored.Category);
        Assert.True(stored.Details is null or { Count: 0 });
    }

    private static AuditEvent AuditWith(string? tenantId, string category) =>
        new(tenantId, AuditEventLevel.Warning, category, "Method 'Ping' of invocation refused")
        {
            Metadata = new Dictionary<string, object?>
            {
                ["invocationId"] = "5f0c2a52-9a17-4b9d-bd37-0d3e7e2f9a10",
                ["method"] = "Basic/Device.Ping",
                ["outcome"] = "Refused",
                ["durationMs"] = 12.5,
                ["skipped"] = null,
            },
        };

    private static void AssertStored(RtEvent stored, string category)
    {
        Assert.Equal(category, stored.Category);
        Assert.NotNull(stored.Details);
        var details = stored.Details!.ToDictionary(d => d.Key, d => d.Value);
        Assert.Equal(4, details.Count); // the null value is skipped
        Assert.Equal("5f0c2a52-9a17-4b9d-bd37-0d3e7e2f9a10", details["invocationId"]);
        Assert.Equal("Basic/Device.Ping", details["method"]);
        Assert.Equal("Refused", details["outcome"]);
        Assert.Equal("12.5", details["durationMs"]);
        Assert.False(details.ContainsKey("skipped"));
    }

    private Task<IReadOnlyList<RtEvent>> ReadAsync(string? tenantId, string category) =>
        QueryAsync(tenantId, nameof(RtEvent.Category), category);

    private Task<IReadOnlyList<RtEvent>> ReadByMessageAsync(string? tenantId, string message) =>
        QueryAsync(tenantId, nameof(RtEvent.Message), message);

    private async Task<IReadOnlyList<RtEvent>> QueryAsync(string? tenantId, string field, string value)
    {
        var systemContext = fixture.SystemContext;
        var repository = tenantId is null
            ? systemContext.GetTenantRepository()
            : await systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var options = RtEntityQueryOptions.Create().FieldFilter(field, FieldFilterOperator.Equals, value);
        var result = await repository.GetRtEntitiesByTypeAsync<RtEvent>(session, options);
        await session.CommitTransactionAsync();
        return result.Items.ToList();
    }
}
