using System.Diagnostics.Metrics;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Audit;

/// <summary>
///     AB#6919: exactly one audit event per invocation, written by the band-100 filter through the shared recorder, with the
///     real authorization (200) and idempotency (300) filters inside it.
/// </summary>
public class CkMethodAuditTests
{
    private static readonly string KeyMaterial = Convert.ToBase64String(Enumerable.Repeat((byte)0x33, 32).ToArray());
    private static readonly CkMethodPrincipal Stranger = new("stranger-1", [], [CkMethodAuthorizationOptions.ApiScope]);

    private class CaptureSink : IAuditEventSink
    {
        public List<AuditEvent> Events { get; } = [];

        public virtual Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StrictFailingSink : CaptureSink, IStrictAuditEventSink
    {
        public override Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask; // the lenient contract swallows

        public Task PublishStrictAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("event repository down");
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

    private sealed class ThrowingFilter(Exception exception) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Idempotency + 1;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            throw exception;
    }

    private sealed class Rig
    {
        public Rig(CaptureSink? sink = null, Action<CkMethodAuditOptions>? configure = null, bool idempotency = true,
            Exception? innerThrows = null)
        {
            Sink = sink ?? new CaptureSink();
            var definition = TestData.Definition();
            Logs = new FakeLogCollector();
            Loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(Logs)));
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
            var auditOptions = new CkMethodAuditOptions();
            configure?.Invoke(auditOptions);
            var recorder = new CkMethodAuditRecorder(Sink, new FixedMonitor<CkMethodAuditOptions>(auditOptions),
                Loggers.CreateLogger<CkMethodAuditRecorder>());
            List<ICkMethodInvocationFilter> filters =
            [
                new CkMethodAuditFilter(recorder, Principals, Loggers.CreateLogger<CkMethodAuditFilter>(), Time),
                new CkMethodAuthorizationFilter(Principals,
                    new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
                    new FixedSource(definition), Loggers.CreateLogger<CkMethodAuthorizationFilter>()),
            ];
            if (idempotency)
            {
                var lifetime = new CkMethodIdempotencyLifetime(Options.Create(new CkMethodIdempotencyOptions()),
                    Loggers.CreateLogger<CkMethodIdempotencyLifetime>());
                filters.Add(new CkMethodIdempotencyFilter(new CkMethodInMemoryIdempotencyStore(lifetime, Time), Principals,
                    new DefinitionSensitivity(definition), Options.Create(new CkMethodDispatcherOptions()), Time,
                    Loggers.CreateLogger<CkMethodIdempotencyFilter>()));
            }

            if (innerThrows != null)
            {
                filters.Add(new ThrowingFilter(innerThrows));
            }

            var keys = new CkMethodFingerprintOptions { Current = new() { Id = "k-1", Secret = KeyMaterial } };
            Pipeline = new CkMethodInvocationPipeline(filters, new DefinitionSensitivity(definition),
                Loggers.CreateLogger<CkMethodInvocationPipeline>(), new CkMethodParametersFingerprinter(Options.Create(keys)));
        }

        public CaptureSink Sink { get; }
        public Principals Principals { get; } = new();
        public FakeLogCollector Logs { get; }
        public ILoggerFactory Loggers { get; }
        public FakeTimeProvider Time { get; }
        public ICkMethodInvocationPipeline Pipeline { get; }
        public int Executions;

        public CkMethodRequest Request(string? key = null, string? parametersJson = null) =>
            TestData.Request(Time.GetUtcNow().UtcDateTime.AddSeconds(30), "meshtest", parametersJson) with
            {
                IdempotencyKey = key,
                Caller = new CkMethodCaller { SubjectId = "request-caller", Name = "Ada Lovelace" },
            };

        public Task<CkMethodResponse> Call(CkMethodRequest request, string transport = CkMethodTransportNames.InProcess,
            Func<CkMethodRequest, CkMethodResponse>? answer = null, CancellationToken token = default) =>
            Pipeline.ExecuteAsync(transport, request, r =>
            {
                Interlocked.Increment(ref Executions);
                return Task.FromResult(answer?.Invoke(r) ?? CkMethodResponse.Succeeded(r.InvocationId, "handler-7", "\"done\""));
            }, token);
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static string Meta(AuditEvent e, string key) =>
        e.Metadata!.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;

    // ---------------------------------------------------------------- exactly one event per outcome

    public static TheoryData<string, string?> Outcomes =>
        new()
        {
            { CkMethodTransportNames.InProcess, null },
            { CkMethodTransportNames.Bus, null },
            { CkMethodTransportNames.InProcess, "WRONG_PASSWORD" },
            { CkMethodTransportNames.Bus, "WRONG_PASSWORD" },
            { CkMethodTransportNames.InProcess, CkMethodErrorCodes.MethodHandlerFailed },
            { CkMethodTransportNames.Bus, CkMethodErrorCodes.MethodHandlerFailed },
            { CkMethodTransportNames.InProcess, CkMethodErrorCodes.MethodTimeout },
            { CkMethodTransportNames.Bus, CkMethodErrorCodes.MethodTimeout },
            { CkMethodTransportNames.InProcess, CkMethodErrorCodes.MethodHandlerNotAvailable },
            { CkMethodTransportNames.Bus, CkMethodErrorCodes.MethodHandlerNotAvailable },
            { CkMethodTransportNames.InProcess, CkMethodErrorCodes.MethodOutcomeUnknown },
            { CkMethodTransportNames.Bus, CkMethodErrorCodes.MethodOutcomeUnknown },
        };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task EveryOutcome_WritesExactlyOneEvent_WithTheFinalOutcome(string transport, string? errorCode)
    {
        var rig = new Rig();
        var request = rig.Request();

        var response = await rig.Call(request, transport, r => errorCode == null
            ? CkMethodResponse.Succeeded(r.InvocationId, "handler-7", "\"secret-result\"")
            : CkMethodResponse.Failed(r.InvocationId, "handler-7", CkMethodError.Create(errorCode)),
            TestContext.Current.CancellationToken);

        var e = Assert.Single(rig.Sink.Events);
        Assert.Equal(errorCode == null, response.Success);
        Assert.Equal(CkMethodAuditEvent.Category, e.Category);
        Assert.Equal("meshtest", e.TenantId);
        Assert.Equal(errorCode ?? CkMethodAuditOutcomes.Success, Meta(e, CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(request.InvocationId.ToString(), Meta(e, CkMethodAuditEvent.Keys.InvocationId));
        Assert.Equal(TestData.MethodId, Meta(e, CkMethodAuditEvent.Keys.MethodId));
        Assert.Equal("System.Identity/User", Meta(e, CkMethodAuditEvent.Keys.CkTypeId));
        Assert.Equal("65f0c0ffee", Meta(e, CkMethodAuditEvent.Keys.TargetRtId));
        Assert.Equal(transport, Meta(e, CkMethodAuditEvent.Keys.Transport));
        Assert.Equal(HostFixture.Administrator.Subject, Meta(e, CkMethodAuditEvent.Keys.CallerSubject)); // the principal, not request.Caller
        Assert.Equal("Ada Lovelace", Meta(e, CkMethodAuditEvent.Keys.CallerName));
        Assert.Equal("handler-7", Meta(e, CkMethodAuditEvent.Keys.HandlerInstanceId));
        Assert.Equal("0", Meta(e, CkMethodAuditEvent.Keys.DurationMs));
        Assert.False(e.Metadata!.ContainsKey(CkMethodAuditEvent.Keys.ReplayOf) && e.Metadata[CkMethodAuditEvent.Keys.ReplayOf] != null);
        Assert.Contains(errorCode ?? CkMethodAuditOutcomes.Success, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, AuditEventLevel.Information)]
    [InlineData("WRONG_PASSWORD", AuditEventLevel.Warning)]
    [InlineData(CkMethodErrorCodes.MethodForbidden, AuditEventLevel.Warning)]
    [InlineData(CkMethodErrorCodes.MethodHandlerFailed, AuditEventLevel.Error)]
    [InlineData(CkMethodErrorCodes.MethodTimeout, AuditEventLevel.Error)]
    [InlineData(CkMethodErrorCodes.MethodOutcomeUnknown, AuditEventLevel.Error)]
    public async Task Level_FollowsTheOutcome(string? errorCode, AuditEventLevel level)
    {
        var rig = new Rig();

        await rig.Call(rig.Request(), answer: r => errorCode == null
            ? CkMethodResponse.Succeeded(r.InvocationId, "h")
            : CkMethodResponse.Failed(r.InvocationId, "h", CkMethodError.Create(errorCode)),
            token: TestContext.Current.CancellationToken);

        Assert.Equal(level, Assert.Single(rig.Sink.Events).Level);
    }

    [Fact]
    public async Task AcceptedOperation_IsRecordedAsAccepted()
    {
        var rig = new Rig();

        await rig.Call(rig.Request(), answer: r => CkMethodResponse.Accepted(r.InvocationId, "h",
            new CkMethodOperationHandle { OperationId = "op-1" }), token: TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodAuditOutcomes.Accepted, Meta(Assert.Single(rig.Sink.Events), CkMethodAuditEvent.Keys.Outcome));
    }

    // ---------------------------------------------------------------- refusal by authorization (band 200)

    [Fact]
    public async Task RefusalByAuthorization_IsAudited_AlthoughTheHandlerNeverRan()
    {
        var rig = new Rig();
        rig.Principals.Current = Stranger;

        var response = await rig.Call(rig.Request(), token: TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, rig.Executions);
        var e = Assert.Single(rig.Sink.Events);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, Meta(e, CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal("stranger-1", Meta(e, CkMethodAuditEvent.Keys.CallerSubject));
        Assert.Equal(AuditEventLevel.Warning, e.Level);
        Assert.Equal(string.Empty, Meta(e, CkMethodAuditEvent.Keys.HandlerInstanceId));
    }

    // ---------------------------------------------------------------- replay (band 300 answers from its store)

    [Fact]
    public async Task Replay_HasItsOwnEvent_WithTheFirstInvocationIdAsReplayOf()
    {
        var rig = new Rig();
        var first = rig.Request("key-1");
        var second = rig.Request("key-1");
        Assert.NotEqual(first.InvocationId, second.InvocationId);

        var r1 = await rig.Call(first, token: TestContext.Current.CancellationToken);
        var r2 = await rig.Call(second, token: TestContext.Current.CancellationToken);

        Assert.True(r1.Success && r2.Success);
        Assert.Equal(1, rig.Executions); // the second call was answered from the store
        Assert.Equal(2, rig.Sink.Events.Count);
        var original = rig.Sink.Events[0];
        var replay = rig.Sink.Events[1];
        Assert.Equal(first.InvocationId.ToString(), Meta(original, CkMethodAuditEvent.Keys.InvocationId));
        Assert.Equal(string.Empty, Meta(original, CkMethodAuditEvent.Keys.ReplayOf));
        Assert.Equal(second.InvocationId.ToString(), Meta(replay, CkMethodAuditEvent.Keys.InvocationId));
        Assert.Equal(first.InvocationId.ToString(), Meta(replay, CkMethodAuditEvent.Keys.ReplayOf));
        Assert.Equal(CkMethodAuditOutcomes.Success, Meta(replay, CkMethodAuditEvent.Keys.Outcome));
        Assert.Contains("(replay)", replay.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParametersChanged_IsAuditedWithItsCode_NotAsReplay()
    {
        var rig = new Rig();
        await rig.Call(rig.Request("key-1"), token: TestContext.Current.CancellationToken);

        var changed = await rig.Call(rig.Request("key-1", "{\"currentPassword\":\"a\",\"newPassword\":\"b\"}"),
            token: TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, changed.Error!.Code);
        Assert.Equal(2, rig.Sink.Events.Count);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, Meta(rig.Sink.Events[1], CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(string.Empty, Meta(rig.Sink.Events[1], CkMethodAuditEvent.Keys.ReplayOf));
    }

    [Fact]
    public async Task RequestInProgress_IsAuditedWithItsCode()
    {
        var rig = new Rig();
        var release = new TaskCompletionSource();
        var firstCall = rig.Pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, rig.Request("key-1"), async r =>
        {
            await release.Task;
            return CkMethodResponse.Succeeded(r.InvocationId, "h");
        }, TestContext.Current.CancellationToken);

        var second = await rig.Call(rig.Request("key-1"), token: TestContext.Current.CancellationToken);
        release.SetResult();
        await firstCall;

        Assert.Equal(CkMethodErrorCodes.MethodRequestInProgress, second.Error!.Code);
        Assert.Equal(2, rig.Sink.Events.Count);
        Assert.Equal(CkMethodErrorCodes.MethodRequestInProgress, Meta(rig.Sink.Events[0], CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(CkMethodAuditOutcomes.Success, Meta(rig.Sink.Events[1], CkMethodAuditEvent.Keys.Outcome));
    }

    // ---------------------------------------------------------------- never parameters, results, tokens, keys

    [Fact]
    public async Task Canary_NoParameterResultTokenKeyOrFingerprint_InTheEvent()
    {
        var rig = new Rig();
        const string idempotencyKey = "idem-key-canary-123";
        await rig.Call(rig.Request(idempotencyKey), answer: r => CkMethodResponse.Succeeded(r.InvocationId, "h",
            "\"result-canary-xyz\""), token: TestContext.Current.CancellationToken);
        await rig.Call(rig.Request(idempotencyKey), token: TestContext.Current.CancellationToken); // replay

        Assert.Equal(2, rig.Sink.Events.Count);
        foreach (var e in rig.Sink.Events)
        {
            var text = e.Message + JsonSerializer.Serialize(e.Metadata) + e.Category + e.TenantId;
            foreach (var canary in new[]
                     {
                         TestData.Secret, TestData.OldSecret, TestData.Token, idempotencyKey, "result-canary-xyz",
                         "currentPassword", "newPassword", KeyMaterial,
                     })
            {
                Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AuditFilterPassingTheUnchangedView_LeavesTheHandlersParametersIntact()
    {
        var rig = new Rig();
        CkMethodRequest? seen = null;

        await rig.Pipeline.ExecuteAsync(CkMethodTransportNames.InProcess, rig.Request(), r =>
        {
            seen = r;
            return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h"));
        }, TestContext.Current.CancellationToken);

        Assert.Contains(TestData.Secret, seen!.ParametersJson, StringComparison.Ordinal);
        Assert.Equal(TestData.Token, seen.CallerAccessToken);
        Assert.DoesNotContain(TestData.Secret, JsonSerializer.Serialize(rig.Sink.Events[0].Metadata), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- exceptions and cancellation

    [Fact]
    public async Task InnerFilterThrows_ExactlyOneHandlerFailedEvent_AndThePipelineAnswersAsBefore()
    {
        var rig = new Rig(innerThrows: new InvalidOperationException("boom"));

        var response = await rig.Call(rig.Request(), token: TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var e = Assert.Single(rig.Sink.Events);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, Meta(e, CkMethodAuditEvent.Keys.Outcome));
        Assert.Equal(AuditEventLevel.Error, e.Level);
        Assert.DoesNotContain("boom", e.Message + JsonSerializer.Serialize(e.Metadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_IsRecordedOnce_AsCallerCancelled_AndStillPropagates()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Pipeline.ExecuteAsync(
            CkMethodTransportNames.InProcess, rig.Request(), async r =>
            {
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
                return CkMethodResponse.Succeeded(r.InvocationId, "h");
            }, cts.Token));

        var e = Assert.Single(rig.Sink.Events);
        Assert.Equal(CkMethodAuditOutcomes.CallerCancelled, Meta(e, CkMethodAuditEvent.Keys.Outcome));
    }

    // ---------------------------------------------------------------- audit failure

    [Fact]
    public async Task AuditSinkDown_DoesNotFailTheCall_ItIsLoggedAndCounted()
    {
        var rig = new Rig(new StrictFailingSink());
        long failures = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == CkMethodAuditRecorder.FailureCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        var response = await rig.Call(rig.Request(), token: TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, rig.Executions);
        Assert.Contains(rig.Logs.GetSnapshot(), r => r.Level == LogLevel.Error &&
                                                      r.Message.Contains("could not be written", StringComparison.Ordinal));
        Assert.True(Interlocked.Read(ref failures) >= 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchOn_AnExecutedCallBecomesOutcomeUnknown_ARefusalStaysARefusal(bool refuse)
    {
        var rig = new Rig(new StrictFailingSink(), o => o.FailCallOnAuditFailure = true);
        if (refuse)
        {
            rig.Principals.Current = Stranger;
        }

        var response = await rig.Call(rig.Request(), token: TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(refuse ? CkMethodErrorCodes.MethodForbidden : CkMethodErrorCodes.MethodOutcomeUnknown,
            response.Error!.Code);
    }

    [Fact]
    public async Task SwitchPerTenant_AppliesOnlyToTheListedTenant()
    {
        var rig = new Rig(new StrictFailingSink(), o => o.FailCallOnAuditFailureTenants = ["OTHERTENANT", "MESHTEST"]);
        var listed = await rig.Call(rig.Request(), token: TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, listed.Error!.Code);

        var rig2 = new Rig(new StrictFailingSink(), o => o.FailCallOnAuditFailureTenants = ["OTHERTENANT"]);
        var unlisted = await rig2.Call(rig2.Request(), token: TestContext.Current.CancellationToken);
        Assert.True(unlisted.Success);
    }

    // ---------------------------------------------------------------- the recorder for the gateway (AB#7826)

    [Fact]
    public async Task Recorder_CanBeUsedWithoutThePipeline_ForAGatewayRefusal()
    {
        var sink = new CaptureSink();
        var recorder = new CkMethodAuditRecorder(sink, new FixedMonitor<CkMethodAuditOptions>(new CkMethodAuditOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CkMethodAuditRecorder>.Instance);
        var invocation = Guid.NewGuid();

        await recorder.RecordAsync(new CkMethodAuditRecord
        {
            TenantId = "meshtest",
            InvocationId = invocation,
            MethodId = TestData.MethodId,
            Transport = CkMethodAuditRecord.GatewayTransport,
            Outcome = CkMethodErrorCodes.MethodTargetNotFound,
            CallerSubject = "user-1\nforged line",
            Duration = TimeSpan.FromMilliseconds(12),
        }, TestContext.Current.CancellationToken);

        var e = Assert.Single(sink.Events);
        Assert.Equal(CkMethodAuditEvent.Category, e.Category);
        Assert.Equal("gateway", Meta(e, CkMethodAuditEvent.Keys.Transport));
        Assert.Equal("12", Meta(e, CkMethodAuditEvent.Keys.DurationMs));
        Assert.Equal("user-1 forged line", Meta(e, CkMethodAuditEvent.Keys.CallerSubject));
        Assert.DoesNotContain('\n', e.Message);
        Assert.Equal(invocation.ToString(), Meta(e, CkMethodAuditEvent.Keys.InvocationId));
    }

    [Fact]
    public void OutcomeOf_UsesTheErrorCode_NeverTheParametersOrTheResult()
    {
        var failed = CkMethodResponse.Failed(Guid.NewGuid(), "h",
            CkMethodError.Create("DECLARED", ("field", "secret-detail")));
        Assert.Equal("DECLARED", CkMethodAuditRecord.OutcomeOf(failed));
        Assert.Equal(CkMethodAuditOutcomes.Success,
            CkMethodAuditRecord.OutcomeOf(CkMethodResponse.Succeeded(Guid.NewGuid(), "h", "\"r\"")));
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public void AddCkMethodAudit_RegistersTheRecorderAndTheFilterOnceAtBand100()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IAuditEventSink>(new CaptureSink());

        services.AddCkMethodAudit();
        services.AddCkMethodAudit();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ICkMethodAuditRecorder>());
        var filter = Assert.Single(provider.GetServices<ICkMethodInvocationFilter>().OfType<CkMethodAuditFilter>());
        Assert.Equal(CkMethodInvocationFilterOrder.Audit, filter.Order);
        Assert.Equal("Methods:Audit", CkMethodAuditOptions.SectionName);
        Assert.False(provider.GetRequiredService<IOptions<CkMethodAuditOptions>>().Value.FailCallOnAuditFailure);
    }
}
