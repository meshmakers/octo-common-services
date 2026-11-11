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
    public async Task StopAsync_DoesNotThrow_WhenTheServiceProviderIsAlreadyDisposed()
    {
        // AB#8872: a WebApplicationFactory disposed twice (or by a derived factory) stops the hosted services after
        // the container is gone; GetService then threw ObjectDisposedException and failed 53 identity test teardowns.
        var inner = A.Fake<IAuditEventSink>();
        var services = new ServiceCollection()
            .AddSingleton(_ => new AggregatingAuditEventSink(inner, Options.Create(new AuditEventAggregationOptions())))
            .BuildServiceProvider();
        var aggregator = services.GetRequiredService<AggregatingAuditEventSink>();
        for (var i = 0; i < 3; i++)
        {
            await DataPermissionAudit.PublishReadViolationAsync(aggregator, "tenantA", "sa-1", "A/A-1", "read");
        }

        var flush = new AuditEventAggregationFlushService(services);
        await services.DisposeAsync();

        await flush.StopAsync(TestContext.Current.CancellationToken);

        // The container's dispose flushed the aggregator: the summary of the 2 repetitions is not lost.
        A.CallTo(() => inner.PublishAsync(
                A<AuditEvent>.That.Matches(e => e.Message.Contains("[aggregated: 2 more time(s)")),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HostStop_AfterTheHostIsDisposed_DoesNotThrow()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(_ =>
            new AggregatingAuditEventSink(A.Fake<IAuditEventSink>(), Options.Create(new AuditEventAggregationOptions())));
        builder.Services.AddHostedService(sp => new AuditEventAggregationFlushService(sp));
        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        // What a second WebApplicationFactory.Dispose does: stop the hosted services of a disposed host again.
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StopAsync(TestContext.Current.CancellationToken);
        }
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
