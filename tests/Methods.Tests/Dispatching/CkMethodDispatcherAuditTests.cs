using System.Diagnostics.Metrics;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Metrics;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

/// <summary>
///     AB#7930: the dispatcher's last line (a fault before the pipeline) and a cancellation that surfaces before the pipeline
///     leave exactly one audit event and one <c>ck.method.calls</c> increment per invocation id, together with the real
///     band-100 filter and pipeline. A call that the pipeline ran is never recorded a second time by the dispatcher.
/// </summary>
public class CkMethodDispatcherAuditTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly IDistributionEventHubService _eventHub = A.Fake<IDistributionEventHubService>();
    private readonly ICkMethodDeclaringElementResolver _resolver = A.Fake<ICkMethodDeclaringElementResolver>();
    private readonly CaptureSink _sink = new();
    private readonly FakeLogCollector _logs = new();
    private readonly string _methodId = $"System.Identity/User.AuditProbe{Guid.NewGuid():N}-1";

    public CkMethodDispatcherAuditTests()
    {
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
    }

    private CkMethodRequest Request() =>
        TestData.Request(_time.GetUtcNow().UtcDateTime.AddSeconds(15)) with { MethodId = _methodId };

    private sealed class CaptureSink : IAuditEventSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRecorder(Exception exception) : ICkMethodAuditRecorder
    {
        public int Calls;

        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw exception;
        }
    }

    private sealed class ThrowingSensitivity : ICkMethodSensitivityResolver
    {
        public IReadOnlySet<string>? GetSensitiveParameters(string tenantId, string ckTypeId, string qualifiedMethodId) =>
            throw new InvalidOperationException("sensitivity lookup fault");
    }

    private sealed class PipelineBackedInProcess(ICkMethodInvocationPipeline pipeline, string methodId)
        : ICkInProcessMethodTransport
    {
        public IReadOnlyCollection<string> MethodIds => [methodId];

        public bool CanHandle(string id) => id == methodId;

        public Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default) =>
            pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, request,
                r => Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "\"x\"")), cancellationToken);
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record Measurement(string Outcome, string Transport);

    private sealed class CallCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _calls = [];

        public CallCounter(string methodId)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CkMethodTelemetry.MeterName && instrument.Name == CkMethodMetrics.CallsName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
                if (map.GetValueOrDefault(CkMethodMetrics.MethodIdTag) == methodId)
                {
                    lock (_calls)
                    {
                        _calls.Add(new Measurement(map[CkMethodMetrics.OutcomeTag]!, map[CkMethodMetrics.TransportTag]!));
                    }
                }
            });
            _listener.Start();
        }

        public List<Measurement> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Rig(CkMethodDispatcher Dispatcher, ICkMethodInvocationPipeline Pipeline);

    /// <summary>The real recorder, audit filter and pipeline; the bus is faked.</summary>
    private Rig Build(bool withAudit = true, ICkMethodSensitivityResolver? pipelineSensitivity = null,
        ICkMethodAuditRecorder? recorder = null, bool inProcess = false)
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        recorder ??= new CkMethodAuditRecorder(_sink, new FixedMonitor<CkMethodAuditOptions>(new CkMethodAuditOptions()),
            loggers.CreateLogger<CkMethodAuditRecorder>());
        var filters = new List<ICkMethodInvocationFilter>();
        if (withAudit)
        {
            filters.Add(new CkMethodAuditFilter(recorder, CkMethodPrincipalScope.Instance,
                loggers.CreateLogger<CkMethodAuditFilter>(), _time));
        }

        var pipeline = new CkMethodInvocationPipeline(filters, pipelineSensitivity ?? new CkMethodSensitivityResolver(null),
            loggers.CreateLogger<CkMethodInvocationPipeline>());
        var services = new ServiceCollection();
        if (withAudit)
        {
            services.AddSingleton(recorder);
            services.AddSingleton<ICkMethodPrincipalProvider>(CkMethodPrincipalScope.Instance);
        }

        if (inProcess)
        {
            services.AddSingleton<ICkInProcessMethodTransport>(new PipelineBackedInProcess(pipeline, _methodId));
        }

        var dispatcher = new CkMethodDispatcher(_resolver, pipeline, new CkMethodHandlerRegistry(_time), _eventHub,
            services.BuildServiceProvider(), new CkMethodSensitivityResolver(null), Options.Create(new CkMethodDispatcherOptions()),
            _time, loggers.CreateLogger<CkMethodDispatcher>());
        return new Rig(dispatcher, pipeline);
    }

    private static string Meta(AuditEvent e, string key) =>
        e.Metadata!.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    [Fact]
    public async Task ResolverFault_RecordsOneEvent_HandlerFailed_OnTheBusTransport_AndCountsOnce()
    {
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Throws<InvalidOperationException>();
        using var counter = new CallCounter(_methodId);
        var request = Request();

        var response = await Build().Dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code); // the caller's answer is unchanged
        var auditEvent = Assert.Single(_sink.Events);
        Assert.Equal(CkMethodAuditEvent.Category, auditEvent.Category);
        Assert.Equal(request.InvocationId.ToString(), Meta(auditEvent, CkMethodAuditEvent.Keys.InvocationId));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, Meta(auditEvent, CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(CkMethodTransportNames.Bus, Meta(auditEvent, CkMethodAuditEvent.Keys.Transport));
        Assert.Equal("sub-1", Meta(auditEvent, CkMethodAuditEvent.Keys.CallerSubject));
        Assert.Equal(string.Empty, Meta(auditEvent, CkMethodAuditEvent.Keys.HandlerInstanceId));
        Assert.Equal(AuditEventLevel.Error, auditEvent.Level);
        var call = Assert.Single(counter.Calls);
        Assert.Equal(new Measurement(CkMethodErrorCodes.MethodHandlerFailed, CkMethodTransportNames.Bus), call);
        Assert.Empty(Fake.GetCalls(_eventHub));
    }

    [Fact]
    public async Task ResolverFaultThatQuotesThePayload_RecordsNoParameterAndNoToken_Canary()
    {
        // the exception of a resolver can quote what it was given: nothing of it may reach the event
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._))
            .Throws(new InvalidOperationException($"cannot resolve {TestData.Secret} {TestData.OldSecret} {TestData.Token}"));

        await Build().Dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        var text = System.Text.Json.JsonSerializer.Serialize(Assert.Single(_sink.Events).Metadata) + Assert.Single(_sink.Events).Message
                   + string.Join("\n", _logs.GetSnapshot().Select(r => r.Message + r.Exception));
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, text);
    }

    [Fact]
    public async Task ExceptionThatEscapesThePipelineBeforeItsFilters_IsRecordedOnce_AndCountedOnlyByThePipeline()
    {
        // the pipeline counts the call in its finally block; no filter ran, so there is no event yet
        using var counter = new CallCounter(_methodId);
        var request = Request();

        var response = await Build(pipelineSensitivity: new ThrowingSensitivity()).Dispatcher
            .InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var auditEvent = Assert.Single(_sink.Events);
        Assert.Equal(request.InvocationId.ToString(), Meta(auditEvent, CkMethodAuditEvent.Keys.InvocationId));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, Meta(auditEvent, CkMethodAuditEvent.Keys.Outcome));
        Assert.Single(counter.Calls);
    }

    [Fact]
    public async Task ExceptionThatEscapesTheInProcessPipeline_IsRecordedOnTheInProcessTransport_OnceAndCountedOnce()
    {
        using var counter = new CallCounter(_methodId);

        var response = await Build(pipelineSensitivity: new ThrowingSensitivity(), inProcess: true).Dispatcher
            .InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(CkMethodTransportNames.InProcess, Meta(Assert.Single(_sink.Events), CkMethodAuditEvent.Keys.Transport));
        Assert.Equal(CkMethodTransportNames.InProcess, Assert.Single(counter.Calls).Transport);
    }

    [Fact]
    public async Task CancellationBeforeThePipeline_IsRecordedOnce_CallerCancelled_AndRethrown()
    {
        using var cts = new CancellationTokenSource();
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Invokes(() => cts.Cancel()).Throws(new OperationCanceledException(cts.Token));
        using var counter = new CallCounter(_methodId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build().Dispatcher.InvokeAsync(Request(), cts.Token));

        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled, Meta(Assert.Single(_sink.Events), CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled, Assert.Single(counter.Calls).Outcome);
    }

    [Fact]
    public async Task CancellationInsideThePipeline_IsRecordedByTheFilterOnly()
    {
        using var cts = new CancellationTokenSource();
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._, A<CkMethodRequest>._,
            A<CommandSendOptions>._, A<CancellationToken>._)).Invokes(() => cts.Cancel()).Throws(new OperationCanceledException(cts.Token));
        using var counter = new CallCounter(_methodId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build().Dispatcher.InvokeAsync(Request(), cts.Token));

        var auditEvent = Assert.Single(_sink.Events);
        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled, Meta(auditEvent, CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(CkMethodTransportNames.Bus, Meta(auditEvent, CkMethodAuditEvent.Keys.Transport));
        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled, Assert.Single(counter.Calls).Outcome);
    }

    [Fact]
    public async Task BusFault_AnsweredInsideThePipeline_IsRecordedByTheFilterOnly_NotAgainByTheDispatcher()
    {
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._, A<CkMethodRequest>._,
            A<CommandSendOptions>._, A<CancellationToken>._)).Throws(new InvalidOperationException("broker"));
        using var counter = new CallCounter(_methodId);

        var response = await Build().Dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Single(_sink.Events);
        Assert.Single(counter.Calls);
    }

    [Fact]
    public async Task WithoutAuditRegistration_TheLastLineStillAnswers_AndCountsOnce()
    {
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Throws<InvalidOperationException>();
        using var counter = new CallCounter(_methodId);

        var response = await Build(withAudit: false).Dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Empty(_sink.Events);
        Assert.Single(counter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailingRecorder_NeverChangesTheAnswer(bool auditFailed)
    {
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Throws<InvalidOperationException>();
        var recorder = new ThrowingRecorder(auditFailed
            ? new CkMethodAuditFailedException(Guid.NewGuid(), new IOException("store"))
            : new InvalidOperationException("recorder"));

        var response = await Build(recorder: recorder).Dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(1, recorder.Calls);
    }
}
