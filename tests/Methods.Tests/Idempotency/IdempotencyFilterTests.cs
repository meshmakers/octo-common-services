using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Idempotency;

/// <summary>AB#7820: the idempotency filter (band 300) behind authorization and the fingerprint refusal.</summary>
public class IdempotencyFilterTests
{
    // synthetic key material, never a real secret
    private static readonly string KeyOne = Convert.ToBase64String(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly string KeyTwo = Convert.ToBase64String(Enumerable.Repeat((byte)0x22, 32).ToArray());

    private sealed class Rig
    {
        public Rig(CkMethodDto? definition = null, ICkMethodIdempotencyStore? store = null,
            CkMethodFingerprintOptions? keys = null, params ICkMethodInvocationFilter[] outer)
        {
            Definition = definition ?? TestData.Definition();
            Logs = new FakeLogCollector();
            Loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(Logs)));
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
            var lifetime = new CkMethodIdempotencyLifetime(Options.Create(new CkMethodIdempotencyOptions()),
                Loggers.CreateLogger<CkMethodIdempotencyLifetime>());
            Memory = new CkMethodInMemoryIdempotencyStore(lifetime, Time);
            Store = store ?? Memory;
            Principal = new SettablePrincipal { Subject = "user-1" };
            var sensitivity = new DefinitionSensitivity(Definition);
            Filter = new CkMethodIdempotencyFilter(Store, Principal, sensitivity,
                Options.Create(new CkMethodDispatcherOptions()), Time, Loggers.CreateLogger<CkMethodIdempotencyFilter>());
            Sensitivity = sensitivity;
            Pipeline = BuildPipeline(keys ?? Keys(KeyOne), [Filter, .. outer]);
        }

        public CkMethodDto Definition { get; }
        public FakeLogCollector Logs { get; }
        public ILoggerFactory Loggers { get; }
        public FakeTimeProvider Time { get; }
        public CkMethodInMemoryIdempotencyStore Memory { get; }
        public ICkMethodIdempotencyStore Store { get; }
        public SettablePrincipal Principal { get; }
        public CkMethodIdempotencyFilter Filter { get; }
        public DefinitionSensitivity Sensitivity { get; }
        public ICkMethodInvocationPipeline Pipeline { get; }

        public int Executions;

        public ICkMethodInvocationPipeline BuildPipeline(CkMethodFingerprintOptions keys,
            IEnumerable<ICkMethodInvocationFilter> filters) =>
            new CkMethodInvocationPipeline(filters, Sensitivity, Loggers.CreateLogger<CkMethodInvocationPipeline>(),
                new CkMethodParametersFingerprinter(Options.Create(keys)));

        public CkMethodRequest Request(string? key = "key-1", string? parametersJson = null, string tenant = "meshtest") =>
            TestData.Request(Time.GetUtcNow().UtcDateTime.AddSeconds(30), tenant, parametersJson) with
            {
                IdempotencyKey = key
            };

        public Task<CkMethodResponse> Call(CkMethodRequest request, Func<CkMethodRequest, Task<CkMethodResponse>>? transport = null,
            ICkMethodInvocationPipeline? pipeline = null) =>
            (pipeline ?? Pipeline).ExecuteAsync(CkMethodTransportNames.InProcess, request, async r =>
            {
                Interlocked.Increment(ref Executions);
                return transport != null
                    ? await transport(r)
                    : CkMethodResponse.Succeeded(r.InvocationId, "handler-1", "\"done\"");
            }, TestContext.Current.CancellationToken);
    }

    private static CkMethodFingerprintOptions Keys(string? current, string? previous = null, string currentId = "k-current",
        string previousId = "k-previous") =>
        new()
        {
            Current = current == null ? new() : new() { Id = currentId, Secret = current },
            Previous = previous == null ? new() : new() { Id = previousId, Secret = previous }
        };

    private sealed class SettablePrincipal : ICkMethodPrincipalProvider
    {
        public string? Subject { get; set; }

        public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) =>
            Subject == null ? null : new CkMethodPrincipal(Subject, [], []);
    }

    private sealed class ThrowingStore(bool claim = true, bool complete = false, bool release = false)
        : ICkMethodIdempotencyStore
    {
        public int Calls;

        public Task<CkMethodIdempotencyClaimResult> ClaimAsync(CkMethodIdempotencyClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return claim
                ? throw new CkMethodIdempotencyStoreException("database down")
                : Task.FromResult(CkMethodIdempotencyClaimResult.ClaimedBy(new CkMethodIdempotencyRecord
                {
                    Id = request.Key.Id,
                    State = CkMethodIdempotencyState.InProgress,
                    MethodId = request.Key.MethodId,
                    FirstInvocationId = request.FirstInvocationId,
                    ClaimedAt = DateTime.UtcNow,
                    LeaseUntil = request.LeaseUntil,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                }));
        }

        public Task<bool> CompleteAsync(string id, Guid firstInvocationId, CkMethodIdempotencyState state,
            CkMethodResponse? response, CancellationToken cancellationToken = default) =>
            complete ? throw new CkMethodIdempotencyStoreException("database down") : Task.FromResult(true);

        public Task<bool> ReleaseAsync(string id, Guid firstInvocationId, CancellationToken cancellationToken = default) =>
            release ? throw new CkMethodIdempotencyStoreException("database down") : Task.FromResult(true);
    }

    private static void AssertRefused(CkMethodResponse response, string reason)
    {
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(CkMethodIdempotencyRefusal.Field, response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal(reason, response.Error.Parameters[CkMethodErrorParameters.Reason]);
    }

    // ---------------------------------------------------------------- unkeyed calls cost nothing

    [Fact]
    public async Task UnkeyedCall_PassesThrough_AndNeverTouchesTheStore()
    {
        var store = new ThrowingStore();
        var rig = new Rig(store: store);
        rig.Principal.Subject = null; // not even the caller is looked at

        var response = await rig.Call(rig.Request(key: null));

        Assert.True(response.Success);
        Assert.Equal(1, rig.Executions);
        Assert.Equal(0, store.Calls);
    }

    // ---------------------------------------------------------------- first call and replay

    [Fact]
    public async Task FirstCallExecutes_AndARepeatReplaysTheFirstResult_WithoutExecuting()
    {
        var rig = new Rig();
        var first = rig.Request();
        var second = rig.Request();
        var facts = new FactsProbe(rig);

        var one = await rig.Call(first);
        var two = await facts.Call(second);

        Assert.True(one.Success);
        Assert.True(two.Success);
        Assert.Equal("\"done\"", two.ResultJson);
        Assert.Equal(second.InvocationId, two.InvocationId); // answers this call, not the first
        Assert.Equal(1, rig.Executions);
        Assert.Equal(first.InvocationId, facts.ReplayOf);
    }

    [Fact]
    public async Task FirstCall_DoesNotSetReplayOf()
    {
        var rig = new Rig();
        var facts = new FactsProbe(rig);

        await facts.Call(rig.Request());

        Assert.Null(facts.ReplayOf);
    }

    /// <summary>Plays the audit filter (band 100): reads the facts after the inner filters ran.</summary>
    private sealed class FactsProbe : ICkMethodInvocationFilter
    {
        private readonly Rig _rig;
        private readonly ICkMethodInvocationPipeline _pipeline;

        public FactsProbe(Rig rig)
        {
            _rig = rig;
            _pipeline = rig.BuildPipeline(Keys(KeyOne), [this, rig.Filter]);
        }

        public Guid? ReplayOf { get; private set; }

        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            var response = await next(context.Request);
            ReplayOf = context.Facts.ReplayOf;
            return response;
        }

        public Task<CkMethodResponse> Call(CkMethodRequest request) => _rig.Call(request, pipeline: _pipeline);
    }

    [Fact]
    public async Task SameKey_OtherParameters_IsRefused_AndTheMethodRunsOnce()
    {
        var rig = new Rig();
        await rig.Call(rig.Request(parametersJson: "{\"currentPassword\":\"a\",\"newPassword\":\"b\"}"));

        var again = await rig.Call(rig.Request(parametersJson: "{\"currentPassword\":\"a\",\"newPassword\":\"OTHER\"}"));

        AssertRefused(again, CkMethodIdempotencyRefusal.ParametersChanged);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task SameKey_ParametersInAnotherSpelling_ReplaysInsteadOfRefusing()
    {
        var rig = new Rig();
        await rig.Call(rig.Request(parametersJson: "{\"currentPassword\":\"a\",\"newPassword\":\"b\"}"));

        var again = await rig.Call(rig.Request(parametersJson: "{ \"newPassword\":\"b\", \"currentPassword\":\"a\" }"));

        Assert.True(again.Success);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task ParametersChangedRefusal_ComesBeforeTheInProgressAnswer()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<CkMethodResponse>();
        var firstRun = rig.Call(rig.Request(parametersJson: "{\"currentPassword\":\"a\",\"newPassword\":\"b\"}"),
            _ => gate.Task);

        var other = await rig.Call(rig.Request(parametersJson: "{\"currentPassword\":\"a\",\"newPassword\":\"c\"}"));
        gate.SetResult(CkMethodResponse.Succeeded(Guid.NewGuid(), "h"));
        await firstRun;

        AssertRefused(other, CkMethodIdempotencyRefusal.ParametersChanged);
    }

    [Fact]
    public async Task TheScopeIsTenantMethodAndSubject_NotTheKeyAlone()
    {
        var rig = new Rig();

        await rig.Call(rig.Request());
        rig.Principal.Subject = "user-2";
        await rig.Call(rig.Request());
        rig.Principal.Subject = "user-1";
        await rig.Call(rig.Request(tenant: "othertenant"));

        Assert.Equal(3, rig.Executions);
        Assert.Equal(3, rig.Memory.Count);
    }

    [Fact]
    public async Task TheSubjectComesFromThePrincipalProvider_NotFromTheInformationalCaller()
    {
        var rig = new Rig();
        var request = rig.Request();
        Assert.Equal("sub-1", request.Caller!.SubjectId);

        await rig.Call(request);
        // the informational caller claims to be somebody else; the authenticated subject is the same -> same scope
        var forged = rig.Request() with { Caller = new CkMethodCaller { SubjectId = "someone-else" } };
        var again = await rig.Call(forged);

        Assert.Equal(1, rig.Executions);
        Assert.True(again.Success);
    }

    // ---------------------------------------------------------------- in flight and the race

    [Fact]
    public async Task WhileTheFirstCallRuns_ARepeatIsInProgress_AndLaterReplays()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<CkMethodResponse>();
        var firstRun = rig.Call(rig.Request(), _ => gate.Task);

        var during = await rig.Call(rig.Request());
        gate.SetResult(CkMethodResponse.Succeeded(Guid.NewGuid(), "h", "\"first\""));
        await firstRun;
        var after = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodRequestInProgress, during.Error!.Code);
        Assert.Equal(TestData.MethodId, during.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.True(after.Success);
        Assert.Equal("\"first\"", after.ResultJson);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task FiftyConcurrentCallsWithOneKey_ExecuteExactlyOnce()
    {
        var rig = new Rig();
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 50).Select(_ => rig.Request()).ToArray();

        var tasks = requests.Select(r => Task.Run(async () =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return await rig.Call(r, async _ =>
            {
                await Task.Delay(30, TestContext.Current.CancellationToken);
                return CkMethodResponse.Succeeded(r.InvocationId, "h", "\"once\"");
            });
        })).ToArray();
        start.Set();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, rig.Executions);
        Assert.All(responses, r => Assert.True(
            r.Success || r.Error!.Code == CkMethodErrorCodes.MethodRequestInProgress, r.ToString()));
        var settled = await rig.Call(rig.Request());
        Assert.Equal("\"once\"", settled.ResultJson);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task AnInFlightClaimPastDeadlinePlusGrace_BurnsTheKey()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<CkMethodResponse>();
        var firstRun = rig.Call(rig.Request(), _ => gate.Task); // deadline +30 s, grace 2 s

        rig.Time.Advance(TimeSpan.FromSeconds(31));
        var inWindow = await rig.Call(rig.Request());
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        var pastLease = await rig.Call(rig.Request());
        gate.SetResult(CkMethodResponse.Succeeded(Guid.NewGuid(), "h"));
        await firstRun;

        Assert.Equal(CkMethodErrorCodes.MethodRequestInProgress, inWindow.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, pastLease.Error!.Code);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task AnAbsurdDeadline_CannotOverflowTheLease_NorMakeItEndless()
    {
        var rig = new Rig();
        var request = rig.Request() with { DeadlineUtc = DateTime.MaxValue };
        var gate = new TaskCompletionSource<CkMethodResponse>();

        var firstRun = rig.Call(request, _ => gate.Task);
        rig.Time.Advance(TimeSpan.FromMinutes(11)); // beyond the 10 minute maximum timeout plus grace
        var repeat = await rig.Call(rig.Request());
        gate.SetResult(CkMethodResponse.Succeeded(Guid.NewGuid(), "h"));
        await firstRun;

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, repeat.Error!.Code);
        Assert.Equal(1, rig.Executions);
    }

    // ---------------------------------------------------------------- which outcomes release or burn

    [Theory]
    [InlineData(CkMethodErrorCodes.MethodHandlerNotAvailable)]
    [InlineData(CkMethodErrorCodes.MethodForbidden)]
    [InlineData(CkMethodErrorCodes.MethodValidationFailed)]
    [InlineData(CkMethodErrorCodes.MethodTargetNotFound)]
    [InlineData(CkMethodErrorCodes.MethodContractVersionUnsupported)]
    [InlineData(CkMethodErrorCodes.MethodParametersTooLarge)]
    public async Task ANotExecutedCode_ReleasesTheKey_SoTheSameKeyRunsAgain(string code)
    {
        var rig = new Rig();
        Task<CkMethodResponse> NotExecuted(CkMethodRequest r) =>
            Task.FromResult(CkMethodResponse.Failed(r.InvocationId, string.Empty, CkMethodError.Create(code)));

        var first = await rig.Call(rig.Request(), NotExecuted);
        var second = await rig.Call(rig.Request());

        Assert.Equal(code, first.Error!.Code);
        Assert.True(second.Success);
        Assert.Equal(2, rig.Executions);
        Assert.Equal(1, rig.Memory.Count);
    }

    [Fact]
    public async Task HandlerFailed_BurnsTheKey_AndARepeatGetsTheStoredFailure_WithoutExecuting()
    {
        var rig = new Rig();
        var failed = CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
            (CkMethodErrorParameters.CorrelationId, "corr-1"));

        var first = await rig.Call(rig.Request(),
            r => Task.FromResult(CkMethodResponse.Failed(r.InvocationId, "h", failed)));
        var second = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, first.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, second.Error!.Code);
        Assert.Equal("corr-1", second.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.Equal(1, rig.Executions);
    }

    [Theory]
    [InlineData(CkMethodErrorCodes.MethodTimeout)]
    [InlineData(CkMethodErrorCodes.MethodOutcomeUnknown)]
    [InlineData("METHOD_SOMETHING_FROM_THE_FUTURE")]
    public async Task AnUnknownOutcome_BurnsTheKey_AndARepeatIsNeitherExecutedNorAnswered(string code)
    {
        var rig = new Rig();

        var first = await rig.Call(rig.Request(), r => Task.FromResult(
            CkMethodResponse.Failed(r.InvocationId, string.Empty, CkMethodError.Create(code))));
        var second = await rig.Call(rig.Request());

        Assert.Equal(code, first.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Null(second.ResultJson);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task AnExceptionInTheTransport_BurnsTheKey()
    {
        var rig = new Rig();

        var first = await rig.Call(rig.Request(), _ => throw new InvalidOperationException("boom"));
        var second = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, first.Error!.Code); // the pipeline's answer
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task ACodeTheMethodDeclares_IsStoredAndReplayed()
    {
        var rig = new Rig();
        Task<CkMethodResponse> Declared(CkMethodRequest r) => Task.FromResult(CkMethodResponse.Failed(
            r.InvocationId, "h", CkMethodError.Create("PASSWORD_TOO_WEAK")));

        await rig.Call(rig.Request(), Declared);
        var second = await rig.Call(rig.Request());

        Assert.Equal("PASSWORD_TOO_WEAK", second.Error!.Code);
        Assert.Equal(1, rig.Executions);
    }

    // ---------------------------------------------------------------- sensitive results

    [Fact]
    public async Task AKeyForAMethodWithASensitiveResult_IsRefused_AndNothingIsStored()
    {
        var definition = TestData.Definition();
        definition.Result!.Sensitive = true;
        var rig = new Rig(definition);

        var response = await rig.Call(rig.Request());

        AssertRefused(response, CkMethodIdempotencyRefusal.ResultSensitive);
        Assert.Equal(0, rig.Executions);
        Assert.Equal(0, rig.Memory.Count);
    }

    [Fact]
    public async Task AKeyForAnUnknownMethod_IsRefusedLikeASensitiveResult()
    {
        var rig = new Rig();
        var unknown = rig.BuildPipeline(Keys(KeyOne), [new CkMethodIdempotencyFilter(rig.Store, rig.Principal,
            new DefinitionSensitivity(null), Options.Create(new CkMethodDispatcherOptions()), rig.Time,
            rig.Loggers.CreateLogger<CkMethodIdempotencyFilter>())]);

        var response = await rig.Call(rig.Request(), pipeline: unknown);

        AssertRefused(response, CkMethodIdempotencyRefusal.ResultSensitive);
        Assert.Equal(0, rig.Memory.Count);
    }

    [Fact]
    public async Task ASensitiveResultMethod_WithoutAKey_StillRuns()
    {
        var definition = TestData.Definition();
        definition.Result!.Sensitive = true;
        var rig = new Rig(definition);

        var response = await rig.Call(rig.Request(key: null));

        Assert.True(response.Success);
        Assert.Equal(1, rig.Executions);
    }

    // ---------------------------------------------------------------- fingerprint keys and rotation

    [Fact]
    public async Task AfterAKeyRotation_AStoredFingerprintOfThePreviousKeyStillReplays()
    {
        var rig = new Rig();
        await rig.Call(rig.Request()); // stored under key id k-current

        var rotated = rig.BuildPipeline(Keys(KeyTwo, KeyOne, "k-new", "k-current"), [rig.Filter]);
        var again = await rig.Call(rig.Request(), pipeline: rotated);

        Assert.True(again.Success);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task AFingerprintStoredUnderAKeyIdThatIsNoLongerConfigured_NeverVerifies()
    {
        var rig = new Rig();
        await rig.Call(rig.Request()); // stored under key id k-current

        var retired = rig.BuildPipeline(Keys(KeyTwo, null, "k-new"), [rig.Filter]);
        var again = await rig.Call(rig.Request(), pipeline: retired);

        AssertRefused(again, CkMethodIdempotencyRefusal.ParametersChanged);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public async Task ARecordWithoutAFingerprint_NeverVerifies()
    {
        var rig = new Rig();
        var request = rig.Request();
        var key = CkMethodIdempotencyKey.Create(request.TenantId, request.MethodId, "user-1", request.IdempotencyKey!);
        await rig.Store.ClaimAsync(new CkMethodIdempotencyClaimRequest(key, Guid.NewGuid(),
            rig.Time.GetUtcNow().UtcDateTime.AddMinutes(1)), TestContext.Current.CancellationToken);

        var response = await rig.Call(request);

        AssertRefused(response, CkMethodIdempotencyRefusal.ParametersChanged);
        Assert.Equal(0, rig.Executions);
    }

    // ---------------------------------------------------------------- refusals before the store

    [Fact]
    public async Task ACallWithoutAnAuthenticatedSubject_IsRefused_NotRunUnkeyed()
    {
        var store = new ThrowingStore();
        var rig = new Rig(store: store);
        rig.Principal.Subject = null;

        var response = await rig.Call(rig.Request());

        AssertRefused(response, CkMethodIdempotencyRefusal.CallerUnknown);
        Assert.Equal(0, rig.Executions);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AKeyLongerThan128Characters_IsRefused()
    {
        var rig = new Rig();

        var response = await rig.Call(rig.Request(new string('k', CkMethodIdempotencyKey.MaxKeyLength + 1)));

        AssertRefused(response, CkMethodIdempotencyRefusal.KeyTooLong);
        Assert.Equal(0, rig.Executions);
    }

    [Fact]
    public async Task WithoutAFingerprint_TheKeyIsRefused()
    {
        // a pipeline without the fingerprinter (the idempotency options were not registered)
        var rig = new Rig();
        var bare = new CkMethodInvocationPipeline([rig.Filter], rig.Sensitivity,
            rig.Loggers.CreateLogger<CkMethodInvocationPipeline>());

        var response = await rig.Call(rig.Request(), pipeline: bare);

        AssertRefused(response, CkMethodIdempotencyRefusal.FingerprintUnavailable);
        Assert.Equal(0, rig.Executions);
        Assert.Equal(0, rig.Memory.Count);
    }

    // ---------------------------------------------------------------- execution.idempotent bypasses the store (AB#7896)

    [Fact]
    public async Task IdempotentMethod_KeyedCalls_NeverTouchTheStore_AndRunEveryTime()
    {
        var store = new ThrowingStore(); // a claim would fail closed, so a pass proves the store was not called
        var rig = new Rig(TestData.Definition().Idempotent(), store);

        var first = await rig.Call(rig.Request("key-1"));
        var second = await rig.Call(rig.Request("key-1"));

        Assert.True(first.Success && second.Success);
        Assert.Equal(2, rig.Executions);
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, rig.Memory.Count);
    }

    [Fact]
    public async Task IdempotentMethod_AcceptsAndIgnoresTheKey_WhateverItLooksLike()
    {
        var rig = new Rig(TestData.Definition().Idempotent());
        var oversized = new string('k', CkMethodIdempotencyKey.MaxKeyLength + 1);

        Assert.True((await rig.Call(rig.Request(oversized))).Success);
        Assert.True((await rig.Call(rig.Request("key with spaces \u00e4"))).Success);
        // the same key with other parameters is not "parameters-changed": there is no fingerprint rule
        Assert.True((await rig.Call(rig.Request("key-1", "{\"hint\":\"a\"}"))).Success);
        Assert.True((await rig.Call(rig.Request("key-1", "{\"hint\":\"b\"}"))).Success);
        Assert.Equal(4, rig.Executions);
        Assert.Equal(0, rig.Memory.Count);
    }

    [Fact]
    public async Task IdempotentMethod_NeedsNoSubject_NoFingerprintKey_AndAllowsASensitiveResult()
    {
        var definition = TestData.Definition().Idempotent();
        definition.Result!.Sensitive = true;
        // no caller subject, and no fingerprint key although the method has sensitive parameters
        var rig = new Rig(definition, keys: Keys(null));
        rig.Principal.Subject = null;

        var response = await rig.Call(rig.Request("key-1"));

        Assert.True(response.Success);
        Assert.Equal(1, rig.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotIdempotentMethod_StillUsesTheStore(bool declaredFalse)
    {
        var definition = TestData.Definition();
        if (declaredFalse)
        {
            definition.Idempotent(false);
        }

        var rig = new Rig(definition);

        await rig.Call(rig.Request("key-1"));
        var replay = await rig.Call(rig.Request("key-1"));

        Assert.True(replay.Success);
        Assert.Equal(1, rig.Executions);
        Assert.Equal(1, rig.Memory.Count);
    }

    [Fact]
    public async Task IdempotentMethod_FilterAloneBypasses_EvenWithoutAFingerprint()
    {
        var rig = new Rig(TestData.Definition().Idempotent());
        var bare = new CkMethodInvocationPipeline([rig.Filter], rig.Sensitivity,
            rig.Loggers.CreateLogger<CkMethodInvocationPipeline>());

        Assert.True((await rig.Call(rig.Request(), pipeline: bare)).Success);
        Assert.Equal(1, rig.Executions);
    }

    [Fact]
    public void Resolver_IsIdempotent_OnlyWhenKnownAndDeclared()
    {
        static ICkMethodAuthorizationDefinitionSource Source(Func<CkMethodDto?> f) => new FuncDefinitionSource(f);

        Assert.True(new CkMethodSensitivityResolver(Source(() => TestData.Definition().Idempotent()))
            .IsIdempotent("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(Source(() => TestData.Definition().Idempotent(false)))
            .IsIdempotent("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(Source(() => TestData.Definition())).IsIdempotent("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(Source(() => null)).IsIdempotent("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(Source(() => throw new InvalidOperationException("boom")))
            .IsIdempotent("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(null).IsIdempotent("t", "T/X", "T/X.M-1"));
    }

    private sealed class FuncDefinitionSource(Func<CkMethodDto?> find) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => find();
    }

    [Fact]
    public async Task AnUnauthorizedCall_NeverReachesTheStore()
    {
        var store = new ThrowingStore(claim: false);
        var denied = new DenyFilter();
        var rig = new Rig(store: store, outer: denied);

        var response = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, rig.Executions);
    }

    private sealed class DenyFilter : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Authorization;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            Task.FromResult(CkMethodResponse.Failed(context.Request.InvocationId, string.Empty,
                CkMethodAuthorizationErrors.Forbidden(context.Request.MethodId)));
    }

    // ---------------------------------------------------------------- fail closed

    [Fact]
    public async Task WhenTheStoreCannotDecide_TheCallIsNotExecuted()
    {
        var rig = new Rig(store: new ThrowingStore());

        var response = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(CkMethodIdempotencyRefusal.StoreUnavailable, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, rig.Executions);
    }

    [Fact]
    public async Task WhenTheStoreFailsAfterTheCallRan_TheCallerStillGetsTheAnswer()
    {
        var rig = new Rig(store: new ThrowingStore(claim: false, complete: true, release: true));

        var ok = await rig.Call(rig.Request());
        var notExecuted = await rig.Call(rig.Request("key-2"), r => Task.FromResult(CkMethodResponse.Failed(
            r.InvocationId, string.Empty, CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable))));

        Assert.True(ok.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, notExecuted.Error!.Code);
        Assert.Equal(2, rig.Executions);
    }

    [Fact]
    public async Task CancellationByTheCaller_Propagates_AndBurnsTheKey()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var request = rig.Request();

        var first = Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Pipeline.ExecuteAsync(
            CkMethodTransportNames.InProcess, request, async _ =>
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            }, cts.Token));
        await first;
        var second = await rig.Call(rig.Request());

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
    }

    // ---------------------------------------------------------------- logging

    [Fact]
    public async Task TheLog_NeverCarriesTheKey_TheFingerprintOrAParameter()
    {
        var rig = new Rig();
        var request = rig.Request("very-secret-idempotency-key-123");

        await rig.Call(request);
        await rig.Call(rig.Request("very-secret-idempotency-key-123"));
        await rig.Call(rig.Request("very-secret-idempotency-key-123", "{\"currentPassword\":\"x\",\"newPassword\":\"y\"}"));

        var text = string.Join("\n", rig.Logs.GetSnapshot().Select(r => r.Message + r.Exception));
        Assert.NotEmpty(text);
        Assert.DoesNotContain("very-secret-idempotency-key-123", text);
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public void AddCkMethodIdempotency_AddsTheFilterOnce_AtBand300()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddCkMethodInMemoryIdempotencyStore();
        services.AddCkMethodIdempotency();
        services.AddCkMethodIdempotency();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

        var filter = Assert.Single(provider.GetServices<ICkMethodInvocationFilter>());
        Assert.IsType<CkMethodIdempotencyFilter>(filter);
        Assert.Equal(CkMethodInvocationFilterOrder.Idempotency, filter.Order);
        Assert.NotNull(provider.GetRequiredService<ICkMethodInvocationPipeline>());
        Assert.NotNull(provider.GetService<ICkMethodParametersFingerprinter>());
    }

    [Fact]
    public void WithoutAStore_ThePipelineCannotBeBuilt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddCkMethodIdempotency();
        using var provider = services.BuildServiceProvider();

        var e = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICkMethodInvocationPipeline>());
        Assert.Contains(nameof(ICkMethodIdempotencyStore), e.Message);
    }
}
