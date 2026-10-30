using System.Diagnostics.Metrics;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Metrics;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Metrics;

/// <summary>
///     AB#6920: the instruments of <c>Meshmakers.Octo.Methods</c> that describe method calls. Every test uses a method id of
///     its own and reads only the measurements that carry it, because the instruments are process-wide and other tests run
///     in parallel.
/// </summary>
public class CkMethodMetricsTests
{
    private static readonly string KeyMaterial = Convert.ToBase64String(Enumerable.Repeat((byte)0x33, 32).ToArray());
    private static readonly CkMethodPrincipal Stranger = new("stranger-1", [], [CkMethodAuthorizationOptions.ApiScope]);

    private static string NewMethodId() => $"Test.Metrics{Guid.NewGuid():N}/Thing.Run-1";

    // ---------------------------------------------------------------- capture

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, string?> Tags)
    {
        public string? Tag(string key) => Tags.GetValueOrDefault(key);
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _all = [];

        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CkMethodTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
            lock (_all)
            {
                _all.Add(new Measurement(instrument.Name, value, copy));
            }
        }

        public List<Measurement> Of(string methodId, string? instrument = null)
        {
            lock (_all)
            {
                return _all.Where(m => m.Tag(CkMethodMetrics.MethodIdTag) == methodId &&
                                       (instrument == null || m.Instrument == instrument)).ToList();
            }
        }

        public List<Measurement> Calls(string methodId) => Of(methodId, CkMethodMetrics.CallsName);

        public void Dispose() => _listener.Dispose();
    }

    // ---------------------------------------------------------------- rig: the real filters around a fake transport

    private sealed class CaptureSink : IAuditEventSink
    {
        public int Events;

        public Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Events);
            return Task.CompletedTask;
        }
    }

    private sealed class Principals : ICkMethodPrincipalProvider
    {
        public CkMethodPrincipal? Current { get; set; } = HostFixture.Administrator;

        public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) => Current;
    }

    private sealed class FixedSource(CkMethodDto? definition) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => definition;
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class Rig
    {
        public Rig()
        {
            var definition = TestData.Definition();
            var loggers = NullLoggerFactory.Instance;
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
            Recorder = new CkMethodAuditRecorder(Sink, new FixedMonitor<CkMethodAuditOptions>(new CkMethodAuditOptions()),
                NullLogger<CkMethodAuditRecorder>.Instance);
            var lifetime = new CkMethodIdempotencyLifetime(Options.Create(new CkMethodIdempotencyOptions()),
                NullLogger<CkMethodIdempotencyLifetime>.Instance);
            List<ICkMethodInvocationFilter> filters =
            [
                new CkMethodAuditFilter(Recorder, Principals, NullLogger<CkMethodAuditFilter>.Instance, Time),
                new CkMethodAuthorizationFilter(Principals,
                    new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
                    new FixedSource(definition), NullLogger<CkMethodAuthorizationFilter>.Instance),
                new CkMethodIdempotencyFilter(new CkMethodInMemoryIdempotencyStore(lifetime, Time), Principals,
                    new DefinitionSensitivity(definition), Options.Create(new CkMethodDispatcherOptions()), Time,
                    NullLogger<CkMethodIdempotencyFilter>.Instance),
            ];
            var keys = new CkMethodFingerprintOptions { Current = new() { Id = "k-1", Secret = KeyMaterial } };
            Pipeline = new CkMethodInvocationPipeline(filters, new DefinitionSensitivity(definition),
                NullLogger<CkMethodInvocationPipeline>.Instance, new CkMethodParametersFingerprinter(Options.Create(keys)));
            _ = loggers;
        }

        public CaptureSink Sink { get; } = new();
        public Principals Principals { get; } = new();
        public FakeTimeProvider Time { get; }
        public CkMethodAuditRecorder Recorder { get; }
        public ICkMethodInvocationPipeline Pipeline { get; }

        public CkMethodRequest Request(string methodId, string? key = null) =>
            TestData.Request(Time.GetUtcNow().UtcDateTime.AddSeconds(30)) with { MethodId = methodId, IdempotencyKey = key };

        public Task<CkMethodResponse> Call(string transport, CkMethodRequest request,
            Func<CkMethodRequest, CkMethodResponse>? answer = null, CancellationToken token = default) =>
            Pipeline.ExecuteAsync(transport, request,
                r => Task.FromResult(answer?.Invoke(r) ?? CkMethodResponse.Succeeded(r.InvocationId, "h", "\"done\"")), token);
    }

    private static CkMethodResponse Failure(CkMethodRequest r, string code, params (string, string)[] parameters) =>
        CkMethodResponse.Failed(r.InvocationId, "h", CkMethodError.Create(code, parameters));

    // ---------------------------------------------------------------- one count per call, every outcome, both transports

    public static TheoryData<string, string?> Outcomes
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var transport in new[] { CkMethodTransportNames.InProcess, CkMethodTransportNames.Bus })
            {
                foreach (var code in new string?[]
                         {
                             null, "WRONG_PASSWORD", CkMethodErrorCodes.MethodHandlerFailed, CkMethodErrorCodes.MethodTimeout,
                             CkMethodErrorCodes.MethodHandlerNotAvailable, CkMethodErrorCodes.MethodOutcomeUnknown,
                             CkMethodErrorCodes.MethodValidationFailed,
                         })
                {
                    data.Add(transport, code);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task EveryOutcome_IsCountedOnce_WithMethodTransportAndOutcome_AndItsDurationIsRecorded(string transport,
        string? errorCode)
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        var response = await rig.Call(transport, rig.Request(methodId),
            r => errorCode == null ? CkMethodResponse.Succeeded(r.InvocationId, "h") : Failure(r, errorCode),
            TestContext.Current.CancellationToken);

        var calls = Assert.Single(capture.Calls(methodId));
        Assert.Equal(1, calls.Value);
        Assert.Equal(transport, calls.Tag(CkMethodMetrics.TransportTag));
        Assert.Equal(errorCode ?? CkMethodAuditOutcomes.Success, calls.Tag(CkMethodMetrics.OutcomeTag));
        Assert.Equal(errorCode == null, response.Success);
        var duration = Assert.Single(capture.Of(methodId, CkMethodMetrics.DurationName));
        Assert.True(duration.Value >= 0);
        Assert.Equal(calls.Tag(CkMethodMetrics.OutcomeTag), duration.Tag(CkMethodMetrics.OutcomeTag));
        // the audit filter wrote its one event as well, and the recorder did not count the call a second time
        Assert.Equal(1, rig.Sink.Events);
    }

    [Fact]
    public async Task AcceptedOperation_IsOutcomeAccepted()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId),
            r => CkMethodResponse.Accepted(r.InvocationId, "h", new CkMethodOperationHandle { OperationId = "op-1" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodAuditOutcomes.Accepted, Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
    }

    [Fact]
    public async Task RefusalByAuthorization_IsCountedOnce_AndTheTransportNeverRan()
    {
        using var capture = new MetricCapture();
        var rig = new Rig { Principals = { Current = Stranger } };
        var methodId = NewMethodId();
        var ran = 0;

        var response = await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId), r =>
        {
            ran++;
            return CkMethodResponse.Succeeded(r.InvocationId, "h");
        }, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(0, ran);
        Assert.Equal(response.Error!.Code, Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
    }

    [Fact]
    public async Task ReplayFromTheIdempotencyStore_IsAnotherCall_CountedOnce()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();
        var executions = 0;
        Func<CkMethodRequest, CkMethodResponse> answer = r =>
        {
            executions++;
            return CkMethodResponse.Succeeded(r.InvocationId, "h", "\"done\"");
        };

        await rig.Call(CkMethodTransportNames.InProcess, rig.Request(methodId, "key-1"), answer,
            TestContext.Current.CancellationToken);
        await rig.Call(CkMethodTransportNames.InProcess, rig.Request(methodId, "key-1"), answer,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, executions);
        var calls = capture.Calls(methodId);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(CkMethodAuditOutcomes.Success, c.Tag(CkMethodMetrics.OutcomeTag)));
    }

    [Fact]
    public async Task ThrowingTransport_IsCountedAsHandlerFailed_AndAnswered()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        var response = await rig.Pipeline.ExecuteAsync(CkMethodTransportNames.Bus, rig.Request(methodId),
            _ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed,
            Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
    }

    [Fact]
    public async Task CallerCancellation_IsCountedOnce_AsCallerCancelled_AndStillThrows()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Pipeline.ExecuteAsync(
            CkMethodTransportNames.Bus, rig.Request(methodId), r => Task.FromCanceled<CkMethodResponse>(cts.Token),
            cts.Token));

        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled,
            Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
    }

    [Fact]
    public async Task PipelineWithoutAnyFilter_IsCountedToo()
    {
        using var capture = new MetricCapture();
        var pipeline = new CkMethodInvocationPipeline([], new CkMethodSensitivityResolver(null),
            NullLogger<CkMethodInvocationPipeline>.Instance);
        var methodId = NewMethodId();

        await pipeline.ExecuteAsync(CkMethodTransportNames.InProcess,
            TestData.Request(DateTime.UtcNow.AddSeconds(30)) with { MethodId = methodId },
            r => Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h")), TestContext.Current.CancellationToken);

        Assert.Single(capture.Calls(methodId));
    }

    // ---------------------------------------------------------------- no_handler

    [Theory]
    [InlineData("no-live-handler", 1)]
    [InlineData("tenant-not-served", 1)]
    [InlineData("method-not-registered", 1)]
    [InlineData(CkMethodIdempotencyRefusal.StoreUnavailable, 0)] // same code, but a store that is down is not a missing handler
    public async Task NoHandler_IsCountedForUnroutableCalls_NotForAStoreThatIsDown(string reason, int expected)
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId),
            r => Failure(r, CkMethodErrorCodes.MethodHandlerNotAvailable, (CkMethodErrorParameters.Reason, reason)),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, capture.Of(methodId, CkMethodMetrics.NoHandlerName).Count);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable,
            Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
        foreach (var m in capture.Of(methodId, CkMethodMetrics.NoHandlerName))
        {
            Assert.Equal(CkMethodTransportNames.Bus, m.Tag(CkMethodMetrics.TransportTag));
            Assert.Equal(1, m.Value);
        }
    }

    private sealed class DispatcherRig
    {
        public DispatcherRig()
        {
            var resolver = A.Fake<ICkMethodDeclaringElementResolver>();
            A.CallTo(() => resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
            Registry = new CkMethodHandlerRegistry(Time);
            var pipeline = new CkMethodInvocationPipeline([], new CkMethodSensitivityResolver(null),
                NullLogger<CkMethodInvocationPipeline>.Instance);
            Dispatcher = new CkMethodDispatcher(resolver, pipeline, Registry, EventHub,
                new ServiceCollection().BuildServiceProvider(), new CkMethodSensitivityResolver(null),
                Options.Create(new CkMethodDispatcherOptions()), Time, NullLogger<CkMethodDispatcher>.Instance);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        public IDistributionEventHubService EventHub { get; } = A.Fake<IDistributionEventHubService>();
        public CkMethodHandlerRegistry Registry { get; }
        public CkMethodDispatcher Dispatcher { get; }

        public CkMethodRequest Request(string methodId) =>
            TestData.Request(Time.GetUtcNow().UtcDateTime.AddSeconds(15)) with { MethodId = methodId };

        public void BusThrows(Exception e) =>
            A.CallTo(() => EventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._)).Throws(e);

        public void BusReturns(CkMethodResponse r) =>
            A.CallTo(() => EventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._)).Returns(r);
    }

    [Fact]
    public async Task Dispatcher_UnroutableCall_CountsNoHandlerOnTheBusTransport()
    {
        using var capture = new MetricCapture();
        var rig = new DispatcherRig();
        var methodId = NewMethodId();
        rig.BusThrows(new CommandNotRoutableException());

        var response = await rig.Dispatcher.InvokeAsync(rig.Request(methodId), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        var calls = Assert.Single(capture.Calls(methodId));
        Assert.Equal(CkMethodTransportNames.Bus, calls.Tag(CkMethodMetrics.TransportTag));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, calls.Tag(CkMethodMetrics.OutcomeTag));
        Assert.Single(capture.Of(methodId, CkMethodMetrics.NoHandlerName));
    }

    [Fact]
    public async Task Dispatcher_HandlerAnswer_IsCountedOnTheBusTransport_AndNotAsNoHandler()
    {
        using var capture = new MetricCapture();
        var rig = new DispatcherRig();
        var methodId = NewMethodId();
        var request = rig.Request(methodId);
        rig.BusReturns(CkMethodResponse.Succeeded(request.InvocationId, "h"));

        await rig.Dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodAuditOutcomes.Success, Assert.Single(capture.Calls(methodId)).Tag(CkMethodMetrics.OutcomeTag));
        Assert.Empty(capture.Of(methodId, CkMethodMetrics.NoHandlerName));
    }

    [Fact]
    public async Task InProcessTransport_CountsTheCallOnce_WithTransportInProcess()
    {
        using var capture = new MetricCapture();
        var methodId = NewMethodId();
        var handler = new ChangePasswordHandler();
        var host = InProcessFixture.With(handler, TestData.Definition(), methodId);

        var response = await host.Transport.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { MethodId = methodId }, TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        var calls = Assert.Single(capture.Calls(methodId));
        Assert.Equal(CkMethodTransportNames.InProcess, calls.Tag(CkMethodMetrics.TransportTag));
        Assert.Equal(CkMethodAuditOutcomes.Success, calls.Tag(CkMethodMetrics.OutcomeTag));
    }

    // ---------------------------------------------------------------- gateway refusals

    [Fact]
    public async Task GatewayRefusal_RecordedThroughTheRecorder_IsCountedOnce_WithTransportGateway()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();
        var record = CkMethodAuditRecord.For(rig.Request(methodId), CkMethodErrorCodes.MethodForbidden,
            CkMethodAuditRecord.GatewayTransport, TimeSpan.FromMilliseconds(3));

        await rig.Recorder.RecordAsync(record, TestContext.Current.CancellationToken);

        var calls = Assert.Single(capture.Calls(methodId));
        Assert.Equal(CkMethodAuditRecord.GatewayTransport, calls.Tag(CkMethodMetrics.TransportTag));
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, calls.Tag(CkMethodMetrics.OutcomeTag));
        Assert.Single(capture.Of(methodId, CkMethodMetrics.DurationName));
        Assert.Equal(1, rig.Sink.Events);
    }

    [Fact]
    public async Task RecorderCall_OfThePipelinesAuditFilter_DoesNotCountAgain()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId), token: TestContext.Current.CancellationToken);

        Assert.Equal(1, rig.Sink.Events);
        Assert.Single(capture.Calls(methodId));
    }

    // ---------------------------------------------------------------- refused duplicates

    private static (CkMethodSeenInvocations Seen, FakeTimeProvider Time) Seen()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        return (new CkMethodSeenInvocations(time), time);
    }

    private static CkMethodRequest DuplicateRequest(FakeTimeProvider time, string methodId) =>
        TestData.Request(time.GetUtcNow().UtcDateTime.AddSeconds(30)) with { MethodId = methodId };

    [Fact]
    public void FlaggedRedelivery_CountsBrokerFlag_WithReasonAndMethodIdOnly()
    {
        using var capture = new MetricCapture();
        var (seen, time) = Seen();
        var methodId = NewMethodId();

        var decision = seen.Decide(DuplicateRequest(time, methodId), TestDelivery.Redelivered);

        Assert.Equal(CkMethodDeliveryDecision.PossibleDuplicate, decision);
        var m = Assert.Single(capture.Of(methodId, CkMethodMetrics.DuplicateRefusedName));
        Assert.Equal(CkMethodDuplicateReasons.BrokerFlag, m.Tag(CkMethodMetrics.ReasonTag));
        Assert.Equal(["method_id", "reason"], m.Tags.Keys.Order().ToArray());
    }

    [Fact]
    public void RepeatedInvocationId_WithoutBrokerFlag_CountsSeenId()
    {
        using var capture = new MetricCapture();
        var (seen, time) = Seen();
        var methodId = NewMethodId();
        var request = DuplicateRequest(time, methodId);

        Assert.Equal(CkMethodDeliveryDecision.Fresh, seen.Decide(request, TestDelivery.First));
        Assert.Empty(capture.Of(methodId, CkMethodMetrics.DuplicateRefusedName));
        Assert.Equal(CkMethodDeliveryDecision.AlreadySeen, seen.Decide(request, TestDelivery.First));

        var m = Assert.Single(capture.Of(methodId, CkMethodMetrics.DuplicateRefusedName));
        Assert.Equal(CkMethodDuplicateReasons.SeenId, m.Tag(CkMethodMetrics.ReasonTag));
    }

    [Fact]
    public void FlaggedRedeliveryOfASeenId_IsCountedOnce_AsBrokerFlag()
    {
        using var capture = new MetricCapture();
        var (seen, time) = Seen();
        var methodId = NewMethodId();
        var request = DuplicateRequest(time, methodId);
        seen.Decide(request, TestDelivery.First);

        seen.Decide(request, TestDelivery.Redelivered);

        var m = Assert.Single(capture.Of(methodId, CkMethodMetrics.DuplicateRefusedName));
        Assert.Equal(CkMethodDuplicateReasons.BrokerFlag, m.Tag(CkMethodMetrics.ReasonTag));
    }

    [Fact]
    public void FreshDelivery_CountsNothing()
    {
        using var capture = new MetricCapture();
        var (seen, time) = Seen();
        var methodId = NewMethodId();

        seen.Decide(DuplicateRequest(time, methodId));

        Assert.Empty(capture.Of(methodId));
    }

    [Fact]
    public async Task BusConsumer_RefusedDuplicate_IsCountedOnTheHandlerHost()
    {
        using var capture = new MetricCapture();
        var methodId = NewMethodId();
        var host = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        var request = TestData.Request(host.InSeconds(30)) with { MethodId = methodId };

        var response = await host.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
        Assert.Equal(CkMethodDuplicateReasons.BrokerFlag,
            Assert.Single(capture.Of(methodId, CkMethodMetrics.DuplicateRefusedName)).Tag(CkMethodMetrics.ReasonTag));
    }

    // ---------------------------------------------------------------- bounded labels

    [Fact]
    public void LabelBudget_AdmitsTheFirstValues_AndReportsFurtherOnesAsOther()
    {
        var budget = new CkMethodLabelBudget(2);

        Assert.Equal("a", budget.Label("a"));
        Assert.Equal("b", budget.Label("b"));
        Assert.Equal(CkMethodMetrics.Other, budget.Label("c"));
        Assert.Equal("a", budget.Label("a")); // admitted stays admitted
        Assert.Equal(CkMethodMetrics.Other, budget.Label(null));
        Assert.Equal(CkMethodMetrics.Other, budget.Label(""));
    }

    [Fact]
    public async Task UnknownTransport_AndMalformedCode_AreMappedToBoundedValues()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();

        await rig.Call("custom-transport", rig.Request(methodId),
            r => Failure(r, "not a code with spaces and {braces} \"quoted\""), TestContext.Current.CancellationToken);

        var calls = Assert.Single(capture.Calls(methodId));
        Assert.Equal(CkMethodMetrics.Other, calls.Tag(CkMethodMetrics.TransportTag));
        Assert.Equal(CkMethodMetrics.InvalidCode, calls.Tag(CkMethodMetrics.OutcomeTag));
    }

    // ---------------------------------------------------------------- canary (shared idea with AB#6914)

    [Fact]
    public async Task Canary_NoParameterResultTokenKeyUserOrEntityId_InAnyLabel()
    {
        using var capture = new MetricCapture();
        var rig = new Rig();
        var methodId = NewMethodId();
        const string idempotencyKey = "idem-key-canary-123";
        const string resultCanary = "result-canary-xyz";

        // success with a result, a replay, an error whose parameters carry the secret, a refusal, a timeout, a failure
        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId, idempotencyKey),
            r => CkMethodResponse.Succeeded(r.InvocationId, "h", $"\"{resultCanary}\""), TestContext.Current.CancellationToken);
        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId, idempotencyKey),
            token: TestContext.Current.CancellationToken);
        await rig.Call(CkMethodTransportNames.InProcess, rig.Request(methodId),
            r => Failure(r, "WRONG_PASSWORD", ("detail", TestData.Secret), ("token", TestData.Token)),
            TestContext.Current.CancellationToken);
        await rig.Call(CkMethodTransportNames.InProcess, rig.Request(methodId),
            r => Failure(r, CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.Reason, TestData.Secret)), TestContext.Current.CancellationToken);
        rig.Principals.Current = Stranger;
        await rig.Call(CkMethodTransportNames.Bus, rig.Request(methodId), token: TestContext.Current.CancellationToken);
        var gateway = CkMethodAuditRecord.For(rig.Request(methodId), CkMethodErrorCodes.MethodValidationFailed,
            CkMethodAuditRecord.GatewayTransport, TimeSpan.Zero);
        await rig.Recorder.RecordAsync(gateway, TestContext.Current.CancellationToken);
        var (seen, time) = Seen();
        var dup = DuplicateRequest(time, methodId) with { CallerAccessToken = TestData.Token };
        seen.Decide(dup, TestDelivery.Redelivered);
        seen.Decide(dup, TestDelivery.First);

        var all = capture.Of(methodId);
        Assert.True(all.Count >= 12);
        string[] allowedKeys = ["method_id", "transport", "outcome", "reason"];
        string[] allowedInstruments =
        [
            CkMethodMetrics.CallsName, CkMethodMetrics.DurationName, CkMethodMetrics.NoHandlerName,
            CkMethodMetrics.DuplicateRefusedName,
        ];
        foreach (var m in all)
        {
            Assert.Contains(m.Instrument, allowedInstruments);
            Assert.All(m.Tags.Keys, key => Assert.Contains(key, allowedKeys));
            var text = m.Instrument + JsonSerializer.Serialize(m.Tags);
            foreach (var canary in new[]
                     {
                         TestData.Secret, TestData.OldSecret, TestData.Token, idempotencyKey, resultCanary, "currentPassword",
                         "newPassword", KeyMaterial, "65f0c0ffee", "sub-1", "request-caller", "Ada Lovelace", "meshtest",
                         dup.InvocationId.ToString(),
                     })
            {
                Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
            }
        }
    }
}
