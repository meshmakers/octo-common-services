using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Runtime.Engine.AuditTrails;
using Meshmakers.Octo.Services.Notifications;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Notifications.Tests;

/// <summary>
///     AB#7279: the event-store path (asset-repo) gets the same aggregation as the adapter log - first violation
///     stored at once, repetitions as one summary - and the last summaries are stored when the host stops.
/// </summary>
public class AuditEventAggregationEventStoreTests
{
    [Fact]
    public async Task RepeatedViolations_AreStoredOnce_AndTheSummaryWhenTheHostStops()
    {
        var repository = A.Fake<IEventRepository>();
        var provider = new ServiceCollection().AddSingleton(repository).BuildServiceProvider();
        var eventStoreSink = new EventRepositoryAuditEventSink(provider, Options.Create(new AuditEventSinkOptions()),
            NullLogger<EventRepositoryAuditEventSink>.Instance);
        await using var aggregator = new AggregatingAuditEventSink(eventStoreSink,
            Options.Create(new AuditEventAggregationOptions()));
        var services = new ServiceCollection().AddSingleton(aggregator).BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        await flush.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 100; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1",
                "Basic.Accounting/BankTransaction-1", DataPermissionAudit.Actions.Read);
        }

        A.CallTo(() => repository.StoreEventAsync("tenantA", A<RtEventSourcesEnum>._, RtEventLevelsEnum.Warning,
                A<string>.That.Not.Contains("[aggregated:"), "DataPermissions.ReadViolation",
                A<IReadOnlyCollection<KeyValuePair<string, string>>?>._, A<RtEntityId?>._))
            .MustHaveHappenedOnceExactly();

        await flush.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() => repository.StoreEventAsync("tenantA", A<RtEventSourcesEnum>._, RtEventLevelsEnum.Warning,
                A<string>.That.Contains("[aggregated: 99 more time(s)"), "DataPermissions.ReadViolation",
                A<IReadOnlyCollection<KeyValuePair<string, string>>?>.That.Matches(d =>
                    d != null && d.Contains(new KeyValuePair<string, string>("AggregatedCount", "99"))),
                A<RtEntityId?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StopAsync_IsBoundedByTheShutdownToken_WhenTheEventStoreHangs()
    {
        var hanging = new TaskCompletionSource();
        var inner = A.Fake<IAuditEventSink>();
        A.CallTo(() => inner.PublishAsync(A<AuditEvent>.That.Matches(e => e.AggregationKey == null), A<CancellationToken>._))
            .Returns(hanging.Task);
        await using var aggregator = new AggregatingAuditEventSink(inner, Options.Create(new AuditEventAggregationOptions()));
        var services = new ServiceCollection().AddSingleton(aggregator).BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        await flush.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1", "A/A-1", "read");
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stop = flush.StopAsync(shutdown.Token);

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        await stop;
        hanging.SetResult();
    }

    [Fact]
    public async Task StopAsync_FlushesWithoutTouchingTheProvider_WhenTheProviderIsAlreadyDisposed()
    {
        // AB#8876: WebApplicationFactory.Dispose stopped the host after its service provider was disposed; the
        // lazy resolve in StopAsync threw ObjectDisposedException out of Host.StopAsync.
        var inner = A.Fake<IAuditEventSink>();
        await using var aggregator = new AggregatingAuditEventSink(inner, Options.Create(new AuditEventAggregationOptions()));
        var services = new ServiceCollection().AddSingleton(aggregator).BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        await flush.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1", "A/A-1", "read");
        }

        await services.DisposeAsync();
        await flush.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() => inner.PublishAsync(A<AuditEvent>.That.Matches(e => e.AggregationKey == null),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StopAsync_DoesNotThrow_WhenTheContainerOwnedAggregatorWasDisposedWithTheProvider()
    {
        // Production wiring: the container owns the aggregator and the event-store sink. Disposing the provider first
        // disposes the aggregator, whose own flush cannot open a scope any more - the summary is lost (error log only),
        // and the host stop afterwards must not throw.
        var repository = A.Fake<IEventRepository>();
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(sp => new EventRepositoryAuditEventSink(sp, Options.Create(new AuditEventSinkOptions()),
                NullLogger<EventRepositoryAuditEventSink>.Instance))
            .AddSingleton(sp => new AggregatingAuditEventSink(sp.GetRequiredService<EventRepositoryAuditEventSink>(),
                Options.Create(new AuditEventAggregationOptions())))
            .BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        await flush.StartAsync(TestContext.Current.CancellationToken);
        var aggregator = services.GetRequiredService<AggregatingAuditEventSink>();
        for (var i = 0; i < 3; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1", "A/A-1", "read");
        }

        await services.DisposeAsync();
        await flush.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() => repository.StoreEventAsync(A<string>._, A<RtEventSourcesEnum>._, A<RtEventLevelsEnum>._,
                A<string>.That.Contains("[aggregated:"), A<string>._,
                A<IReadOnlyCollection<KeyValuePair<string, string>>?>._, A<RtEntityId?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task StopAsync_FlushesPendingSummaries_WhenStartAsyncNeverRan()
    {
        // An earlier hosted service failed to start, so this one never started; the explicit host stop still flushes.
        var inner = A.Fake<IAuditEventSink>();
        await using var aggregator = new AggregatingAuditEventSink(inner, Options.Create(new AuditEventAggregationOptions()));
        var services = new ServiceCollection().AddSingleton(aggregator).BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        for (var i = 0; i < 3; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1", "A/A-1", "read");
        }

        await flush.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() => inner.PublishAsync(A<AuditEvent>.That.Matches(e => e.AggregationKey == null),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StopAsync_DoesNothing_WhenTheHostNeverStarted_AndTheProviderIsDisposed()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var flush = new AuditEventAggregationFlushService(services);
        await services.DisposeAsync();

        await flush.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AddOctoNotification_RegistersTheFlushOnStop()
    {
        var services = new ServiceCollection();
        services.AddOctoNotification();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) &&
                                       d.ImplementationType == typeof(AuditEventAggregationFlushService));
    }
}
