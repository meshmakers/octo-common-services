using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     AB#6920 against a private RabbitMQ: a call over the bus is ONE trace from the gateway span through the dispatcher's
///     call span and MassTransit's send/process spans to the handler span, and the meter reports it. The tracer provider is
///     the one <c>AddObservability()</c> builds (so the test fails when <c>AddSource("MassTransit")</c> is removed), with an
///     in-memory exporter added.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodTraceAndMetricsBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string MethodId = "Sample.Traces/Thing.Run-1";
    private const string Tenant = "meshtest";
    private readonly string _instancePrefix = $"trace-{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<IEventHubControl> _hubs = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
        {
            try
            {
                await hub.StopAsync();
            }
            catch
            {
                // best effort
            }
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    public sealed record ThingRunParameters
    {
        public required string Note { get; init; }
    }

    public sealed class HandlerProbe
    {
        public Activity? ActivityAtEntry;
    }

    public sealed class RunHandler(HandlerProbe probe) : ICkMethodHandler<ThingRunParameters>
    {
        public Task<CkMethodOutcome> HandleAsync(CkMethodContext<ThingRunParameters> context, CancellationToken cancellationToken)
        {
            probe.ActivityAtEntry = Activity.Current;
            return Task.FromResult(CkMethodOutcome.Ok());
        }
    }

    private sealed class DefinitionSource : ICkMethodDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) => qualifiedMethodId != MethodId
            ? null
            : new CkMethodDto
            {
                MethodId = "Run-1",
                Parameters = [new CkMethodParameterDto { Name = "note", ValueType = AttributeValueTypesDto.String }],
            };
    }

    private ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = _instancePrefix;
            o.BrokerHost = rabbitMq.Host;
            o.BrokerPort = (ushort)rabbitMq.Port;
            o.BrokerUser = "guest";
            o.BrokerPassword = "guest";
        });
        return services;
    }

    private async Task StartAsync(ServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var hub = provider.GetRequiredService<IEventHubControl>();
        _hubs.Add(hub);
        await hub.StartAsync();
        using (var scope = provider.CreateScope())
        {
            foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
            {
                await service.InitializeAsync();
            }
        }

        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private async Task<(ICkMethodDispatcher Dispatcher, HandlerProbe Probe)> StartHostsAsync(bool withHandler = true)
    {
        var probe = new HandlerProbe();
        if (withHandler)
        {
            var handlerHost = BaseServices();
            handlerHost.AddSingleton(probe);
            handlerHost.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
            handlerHost.AddDistributionEventHub(c =>
            {
                c.UniqueServiceAddress = "TraceHandler";
                c.AutomaticallyStartBusDuringStartup = false;
                c.AddCkMethodHandlerDiscovery();
            });
            handlerHost.AddSingleton<ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
            handlerHost.AddCkMethodHandlers("TraceHandler", m => m.Handle<RunHandler>(MethodId));
            await StartAsync(handlerHost);
        }

        var gateway = BaseServices();
        gateway.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        gateway.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "TraceGateway";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        gateway.AddCkMethodDispatcher();
        await StartAsync(gateway);
        return (_providers[^1].GetRequiredService<ICkMethodDispatcher>(), probe);
    }

    private static CkMethodRequest NewRequest(string methodId = MethodId) => new(Tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = methodId,
        CkTypeId = "Sample.Traces/Thing",
        TargetRtId = "thing-1",
        ParametersJson = JsonSerializer.Serialize(new { note = "hello" }),
        DeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task CallOverTheBus_IsOneTrace_GatewaySpan_CallSpan_BusSpans_HandlerSpan()
    {
        var spans = new List<Activity>();
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();
        builder.Services.ConfigureOpenTelemetryTracerProvider((_, b) => b.AddSource("test.gateway").AddInMemoryExporter(spans));
        using var otel = builder.Build();
        var tracer = otel.Services.GetRequiredService<TracerProvider>();
        var (dispatcher, probe) = await StartHostsAsync();

        string traceId;
        string gatewaySpanId;
        using (var gatewaySource = new ActivitySource("test.gateway"))
        using (var gateway = gatewaySource.StartActivity("gateway request", ActivityKind.Server))
        {
            Assert.NotNull(gateway);
            traceId = gateway.TraceId.ToString();
            gatewaySpanId = gateway.SpanId.ToString();
            var response = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
            Assert.True(response.Success, response.Error?.Code);
        }

        Assert.True(tracer.ForceFlush(10_000));
        // the code inside the handler runs in the trace of the gateway
        Assert.NotNull(probe.ActivityAtEntry);
        Assert.Equal(traceId, probe.ActivityAtEntry.TraceId.ToString());

        var inTrace = spans.Where(s => s.TraceId.ToString() == traceId).ToList();
        var call = Assert.Single(inTrace, s => s.OperationName == "CK method call");
        var handle = Assert.Single(inTrace, s => s.OperationName == "CK method handle");
        Assert.Equal(gatewaySpanId, call.ParentSpanId.ToString());
        Assert.Equal(CkMethodTransportNames.Bus, call.GetTagItem("ck.method.transport"));
        Assert.Equal(MethodId, call.GetTagItem("ck.method.id"));
        Assert.Equal("SUCCESS", call.GetTagItem("ck.method.outcome"));
        Assert.Equal(MethodId, handle.GetTagItem("ck.method.id"));

        // the handler span descends from the call span through the bus: walk up by parent span id
        var bySpanId = inTrace.ToDictionary(s => s.SpanId.ToString());
        var busSpans = 0;
        var cursor = handle;
        var reachedCall = false;
        while (cursor.ParentSpanId != default && bySpanId.TryGetValue(cursor.ParentSpanId.ToString(), out var parent))
        {
            if (parent.Source.Name == "MassTransit")
            {
                busSpans++;
            }

            if (parent.SpanId == call.SpanId)
            {
                reachedCall = true;
                break;
            }

            cursor = parent;
        }

        Assert.True(reachedCall, "the handler span does not descend from the call span");
        Assert.True(busSpans >= 1, "no MassTransit span between the call span and the handler span");
        // nothing from the call leaked into a second trace
        Assert.DoesNotContain(spans, s => s.OperationName == "CK method handle" && s.TraceId.ToString() != traceId);
    }

    [Fact]
    public async Task CallsOverTheBus_AreCounted_SuccessAndNoHandler()
    {
        var measurements = new List<(string Name, double Value, Dictionary<string, string?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == CkMethodTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((i.Name, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())));
            }
        });
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((i.Name, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())));
            }
        });
        listener.Start();
        var (dispatcher, _) = await StartHostsAsync();
        const string unregistered = "Sample.Traces/Thing.Unregistered-1";

        var ok = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        var none = await dispatcher.InvokeAsync(NewRequest(unregistered), TestContext.Current.CancellationToken);

        Assert.True(ok.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, none.Error!.Code);
        List<(string Name, double Value, Dictionary<string, string?> Tags)> snapshot;
        lock (measurements)
        {
            snapshot = [.. measurements];
        }

        Assert.Single(snapshot, m => m.Name == "ck.method.calls" && m.Tags["method_id"] == MethodId &&
                                     m.Tags["transport"] == "bus" && m.Tags["outcome"] == "SUCCESS");
        Assert.Single(snapshot, m => m.Name == "ck.method.calls" && m.Tags["method_id"] == unregistered &&
                                     m.Tags["outcome"] == "METHOD_HANDLER_NOT_AVAILABLE");
        Assert.Single(snapshot, m => m.Name == "ck.method.no_handler" && m.Tags["method_id"] == unregistered);
        Assert.DoesNotContain(snapshot, m => m.Name == "ck.method.no_handler" && m.Tags["method_id"] == MethodId);
        Assert.Equal(2, snapshot.Count(m => m.Name == "ck.method.duration"));
    }
}
