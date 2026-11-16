using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Methods;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

/// <summary>Dispatcher logic against a faked event hub; the real broker is covered by the integration tests.</summary>
public class CkMethodDispatcherTests
{
    private const string RemoteMethod = "System.Identity/User.ChangePassword-1";
    private const string UnannouncedMethod = "System.Identity/User.CreateUser-1";
    private const string DefaultInstance = "default-live";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero)); // same start as InProcessFixture
    private readonly FakeLogCollector _logs = new();
    private readonly IDistributionEventHubService _eventHub = A.Fake<IDistributionEventHubService>();
    private readonly ICkMethodDeclaringElementResolver _resolver = A.Fake<ICkMethodDeclaringElementResolver>();
    private readonly CkMethodHandlerRegistry _registry;
    private readonly CkMethodDispatcherOptions _options = new();
    private readonly List<ICkMethodInvocationFilter> _filters = [];
    private ICkInProcessMethodTransport? _inProcess;
    private ICkMethodInvocationPipeline? _pipeline;
    private CkMethodHandlerDiscoveryRequester? _discovery;

    public CkMethodDispatcherTests()
    {
        _registry = new CkMethodHandlerRegistry(_time, Options.Create(_options));
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
        // AB#8634: the bus send needs a live handler in the registry; the default one lives for the whole test
        AnnounceDefaultHandler();
    }

    private void AnnounceDefaultHandler() =>
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
        {
            ServiceName = "identity", InstanceId = DefaultInstance, TtlSeconds = 24 * 3600,
            MethodIds = [RemoteMethod, "System.Identity/Principal.ChangePassword-1", "System.Identity/User.Other-1"]
        });

    /// <summary>Withdraws the default handler, so only what the test announces is live.</summary>
    private void WithoutDefaultHandler() =>
        _registry.OnWithdrawn(new CkMethodHandlerWithdrawn(Guid.NewGuid(), Now)
        {
            ServiceName = "identity", InstanceId = DefaultInstance,
            MethodIds = [RemoteMethod, "System.Identity/Principal.ChangePassword-1", "System.Identity/User.Other-1"]
        });

    /// <summary>Lets the registry warm-up run out.</summary>
    private void AfterWarmUp() => _time.Advance(_options.RegistryWarmUp);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodRequest Request(int deadlineSeconds = 15, string methodId = RemoteMethod) =>
        TestData.Request(Now.AddSeconds(deadlineSeconds)) with { MethodId = methodId };

    private CkMethodDispatcher Dispatcher()
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        _pipeline ??= new CkMethodInvocationPipeline(_filters, new CkMethodSensitivityResolver(null), loggers.CreateLogger<CkMethodInvocationPipeline>());
        var services = new ServiceCollection();
        if (_inProcess != null)
        {
            services.AddSingleton<ICkInProcessMethodTransport>(_inProcess);
        }

        _discovery ??= new CkMethodHandlerDiscoveryRequester(_eventHub, _time,
            loggers.CreateLogger<CkMethodHandlerDiscoveryRequester>());
        return new CkMethodDispatcher(_resolver, _pipeline, _registry, _eventHub, services.BuildServiceProvider(),
            new CkMethodSensitivityResolver(null), Options.Create(_options), _time, loggers.CreateLogger<CkMethodDispatcher>(),
            _discovery);
    }

    private void BusReturns(CkMethodResponse response) =>
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._, A<CkMethodRequest>._,
                A<CommandSendOptions>._, A<CancellationToken>._))
            .Returns(response);

    private void BusThrows(Exception exception) =>
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._, A<CkMethodRequest>._,
                A<CommandSendOptions>._, A<CancellationToken>._))
            .Throws(exception);


    private CommandSendOptions SentOptions()
    {
        var call = Fake.GetCalls(_eventHub).Single(c => c.Method.Name == "GetCommandResponseAsync");
        return (CommandSendOptions)call.Arguments[2]!;
    }

    private CkMethodRequest SentRequest() =>
        (CkMethodRequest)Fake.GetCalls(_eventHub).Single(c => c.Method.Name == "GetCommandResponseAsync").Arguments[1]!;

    // the event hub creates it internally (private constructors)
    private static Exception NewTimeoutException() =>
        (Exception)Activator.CreateInstance(typeof(DistributionTimeoutException), nonPublic: true)!;

    private int Sends() => Fake.GetCalls(_eventHub).Count(c => c.Method.Name == "GetCommandResponseAsync");

    /// <summary>Discovery requests published by the dispatcher (they run in the background).</summary>
    private int DiscoveryRequests() => Fake.GetCalls(_eventHub).Count(c => c.Method.Name == "PublishAsync" &&
                                                                         c.Arguments.FirstOrDefault() is CkMethodHandlerDiscoveryRequested);

    private async Task WaitForDiscoveryRequestsAsync(int expected)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DiscoveryRequests() < expected && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, DiscoveryRequests());
    }

    /// <summary>
    ///     Advances the fake clock in steps until the task completes (at most 60 s of fake time) and returns how much
    ///     time passed. For a call whose wait is re-armed on another thread after a registry signal.
    /// </summary>
    private async Task<TimeSpan> AdvanceUntilCompletedAsync(Task task, TimeSpan step)
    {
        var advanced = TimeSpan.Zero;
        while (!task.IsCompleted && advanced < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            if (task.IsCompleted)
            {
                break;
            }

            _time.Advance(step);
            advanced += step;
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return advanced;
    }

    /// <summary>Lets the hand-over after the last handler went away run out.</summary>
    private void AfterTheHandOver() => _time.Advance(_options.HandlerHandOver);

    [Fact]
    public async Task Bus_ReturnsTheHandlersEnvelopeUnchanged_ForSuccessDeclaredErrorAndFailure()
    {
        var request = Request();
        CkMethodResponse[] envelopes =
        [
            CkMethodResponse.Succeeded(request.InvocationId, "h1", "{\"value\":\"x\"}"),
            CkMethodResponse.Failed(request.InvocationId, "h1",
                CkMethodError.Create("PASSWORD_TOO_SHORT", ("minLength", "8"))),
            CkMethodResponse.Failed(request.InvocationId, "h1",
                CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                    (CkMethodErrorParameters.MethodId, RemoteMethod), (CkMethodErrorParameters.CorrelationId, "c1")))
        ];
        foreach (var envelope in envelopes)
        {
            Fake.ClearRecordedCalls(_eventHub);
            BusReturns(envelope);

            var response = await Dispatcher().InvokeAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(JsonSerializer.Serialize(envelope, Wire), JsonSerializer.Serialize(response, Wire));
            Assert.Equal(CkMethodQueueNames.For(RemoteMethod),
                (string)Fake.GetCalls(_eventHub).Single().Arguments[0]!);
        }
    }

    [Fact]
    public async Task Bus_SendsOnce_Mandatory_NonPersistent_WithTtlAndTimeoutFromTheDeadline()
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request(deadlineSeconds: 20), TestContext.Current.CancellationToken);

        var options = SentOptions();
        Assert.Equal(1, Sends());
        Assert.True(options.Mandatory);
        Assert.False(options.Persistent);
        Assert.Equal(TimeSpan.FromSeconds(22), options.Timeout); // deadline + 2 s grace
        Assert.Equal(options.Timeout, options.TimeToLive);
    }

    [Fact]
    public async Task NoConsumer_NotRoutable_IsHandlerNotAvailable_WithMethodIdAndReason_NoStackTrace()
    {
        BusThrows(new CommandNotRoutableException("returned by the broker at some.stack.Frame"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(RemoteMethod, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal("no-live-handler", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(string.Empty, response.HandlerInstanceId);
        Assert.DoesNotContain("some.stack.Frame", JsonSerializer.Serialize(response, Wire));
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task NotRoutable_MarksTheRegistryEntryExpired()
    {
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "a", MethodIds = [RemoteMethod] });
        _time.Advance(TimeSpan.FromSeconds(1));
        BusThrows(new CommandNotRoutableException());

        await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Empty(_registry.GetLiveHandlers(RemoteMethod));
        Assert.True(_registry.GetLastSeen(RemoteMethod)!.NoQueueBound);
    }

    [Fact]
    public async Task NoLiveHandler_AfterTheWarmUp_IsRefused_WithoutAnythingSentToTheBroker()
    {
        AfterWarmUp();

        var response = await Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);

        // same envelope as the broker's return before AB#8634
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(UnannouncedMethod, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal("no-live-handler", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(string.Empty, response.HandlerInstanceId);
        Assert.Empty(Fake.GetCalls(_eventHub)); // nothing at all went to the event hub
        Assert.Contains(_logs.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains("no-live-handler"));
    }

    [Fact]
    public async Task WithdrawnHandler_IsRefused_WithoutSending_OnceTheHandOverEnded()
    {
        AfterWarmUp();
        WithoutDefaultHandler();
        AfterTheHandOver();

        var call = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(call.IsCompleted); // after the hand-over a withdrawal is an answer: no wait at all
        var response = await call;

        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_AWithdrawalTheRegistryReceived_WaitsOnlyForTheHandOver_NotForTheWarmUp()
    {
        WithoutDefaultHandler(); // a withdrawal is an answer: only the hand-over is waited for

        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 60), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);

        AfterTheHandOver(); // well before the warm-up ends
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task ExpiredHandler_IsRefused_WithoutSending()
    {
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "device", InstanceId = "d", MethodIds = [UnannouncedMethod], TtlSeconds = 45 });
        _time.Advance(TimeSpan.FromSeconds(45) + _options.HandlerHandOver); // also past the warm-up and the hand-over

        var call = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);
        Assert.True(call.IsCompleted);
        var response = await call;

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task AfterTheBrokerReturnedASend_TheNextCallIsRefused_WithoutSending_WhenNoHandlerAnswers()
    {
        AfterWarmUp();
        BusThrows(new CommandNotRoutableException()); // the live entry was stale (handler crashed inside its TTL)

        var first = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        var second = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted); // waits for the hand-over: nobody answers the discovery request
        AfterTheHandOver();
        var response = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("no-live-handler", first.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(1, Sends());

        // and after the hand-over the method costs no wait any more
        var third = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(third.IsCompleted);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_ACallWaitsForTheAnnouncement_AndIsSent()
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        var call = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        Assert.Equal(0, Sends());

        _time.Advance(TimeSpan.FromSeconds(1));
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "late", MethodIds = [UnannouncedMethod] });
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, Sends());
        // the wait used up one second of the 15 s deadline
        Assert.Equal(TimeSpan.FromSeconds(14) + _options.ResponseGrace, SentOptions().Timeout);
    }

    [Fact]
    public async Task DuringTheWarmUp_WithoutAnnouncement_TheCallIsRefusedWhenTheWarmUpEnds_WithoutSending()
    {
        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 60, methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);
        _time.Advance(_options.RegistryWarmUp - TimeSpan.FromSeconds(1));
        Assert.False(call.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_AnAnnouncementOfAnotherMethod_DoesNotEndTheWait()
    {
        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 60, methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);

        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "other", InstanceId = "o", MethodIds = ["Other/Thing.Do-1"] });
        await Task.Yield();
        Assert.False(call.IsCompleted);

        AfterWarmUp();
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_AWithdrawalArrivingWhileTheCallWaits_EndsTheWaitWithTheHandOver()
    {
        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 60, methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);

        // the withdrawal wakes the waiting call; from now on it waits only for the hand-over, not the whole warm-up
        _registry.OnWithdrawn(new CkMethodHandlerWithdrawn(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "gone", MethodIds = [UnannouncedMethod] });
        await Task.Yield();
        Assert.False(call.IsCompleted);
        // the woken call sets up its next timer on another thread: advance in steps instead of racing it
        var advanced = await AdvanceUntilCompletedAsync(call, TimeSpan.FromMilliseconds(250));
        Assert.InRange(advanced, _options.HandlerHandOver - TimeSpan.FromMilliseconds(250), _options.HandlerHandOver + TimeSpan.FromSeconds(1));
        var response = await call;

        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_TheWaitEndsAtTheRequestDeadline()
    {
        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 3, methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromSeconds(3));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task DuringTheWarmUp_CancellationOfTheCaller_Surfaces()
    {
        using var cts = new CancellationTokenSource();
        var call = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, Sends());
    }

    [Fact]
    public void WarmUpZero_EndsTheWarmUpAtOnce()
    {
        _options.RegistryWarmUp = TimeSpan.Zero;
        var registry = new CkMethodHandlerRegistry(_time, Options.Create(_options));

        Assert.Equal(Now, registry.WarmUpEndsUtc);
    }

    [Fact]
    public async Task EmergencySwitchOff_EmptyRegistry_DoesNotRefuse_TheBrokerDecides()
    {
        _options.RefuseWithoutLiveHandler = false;
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        var response = await Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task HandlersOnlyForOtherTenants_AreNotAvailable_WithoutSending()
    {
        WithoutDefaultHandler();
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "a", MethodIds = [RemoteMethod], Tenants = ["other"] });

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal("tenant-not-served", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task RequestNewerThanTheAnnouncedContractVersion_IsRefusedWithoutSending()
    {
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "a", MethodIds = [RemoteMethod], ContractVersion = 1 });

        var response = await Dispatcher().InvokeAsync(Request() with { ContractVersion = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodContractVersionUnsupported, response.Error!.Code);
        Assert.Equal("2", response.Error.Parameters[CkMethodErrorParameters.RequestedVersion]);
        Assert.Equal("1", response.Error.Parameters[CkMethodErrorParameters.SupportedVersion]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task NoAnswerBeforeTheDeadline_IsMethodTimeout_OutcomeUnknown_AndNeverSentTwice()
    {
        BusThrows(NewTimeoutException());

        var response = await Dispatcher().InvokeAsync(Request(deadlineSeconds: 7), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal("7", response.Error.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
        Assert.Equal(RemoteMethod, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(1, Sends()); // no resend on a transport fault
    }

    [Fact]
    public async Task DeadlineAlreadyPassed_IsTimeout_WithoutSending()
    {
        var response = await Dispatcher().InvokeAsync(Request(deadlineSeconds: -1), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AbsurdDeadline_IsClampedToMaxTimeout_AndNeverThrows(bool maxValueKind)
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));
        var deadline = maxValueKind ? DateTime.MaxValue : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        var response = await Dispatcher().InvokeAsync(Request() with { DeadlineUtc = deadline },
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(_options.MaxTimeout + _options.ResponseGrace, SentOptions().Timeout);
        // the handler sees the clamped deadline, not the absurd one
        Assert.Equal(Now + _options.MaxTimeout, SentRequest().DeadlineUtc);
    }

    [Fact]
    public async Task TimeoutSeconds_IsFilledFromTheDeadline_WhenTheCallerLeftItOut()
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request(deadlineSeconds: 7), TestContext.Current.CancellationToken);

        Assert.Equal(7, SentRequest().TimeoutSeconds);
    }

    [Fact]
    public async Task TimeoutSeconds_OfTheCaller_IsKept_AndReportedByTheDispatchersOwnTimeout()
    {
        BusThrows(NewTimeoutException());

        var response = await Dispatcher().InvokeAsync(Request(deadlineSeconds: 2) with { TimeoutSeconds = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, SentRequest().TimeoutSeconds);
        Assert.Equal("2", response.Error!.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 300)]
    public async Task TimeoutSeconds_OutsideTheCompilerRange_IsClampedDefensively(int given, int expected)
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request() with { TimeoutSeconds = given }, TestContext.Current.CancellationToken);

        Assert.Equal(expected, SentRequest().TimeoutSeconds);
    }

    [Fact]
    public async Task AbsurdDeadline_IsClampedBeforeTheFilters_AndTheTimeoutSecondsStayWithinTheCompilerRange()
    {
        DateTime? seenByFilter = null;
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Audit, (ctx, next) =>
        {
            seenByFilter = ctx.Request.DeadlineUtc;
            return next(ctx.Request);
        }));
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request() with { DeadlineUtc = DateTime.MaxValue },
            TestContext.Current.CancellationToken);

        Assert.Equal(Now + _options.MaxTimeout, seenByFilter);
        Assert.Equal(300, SentRequest().TimeoutSeconds);
    }

    [Fact]
    public async Task AbsurdDeadlineSetByAFilter_IsClampedAgainBeforeTheSend()
    {
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Audit,
            (ctx, next) => next(ctx.Request with { DeadlineUtc = DateTime.MaxValue })));
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(_options.MaxTimeout + _options.ResponseGrace, SentOptions().Timeout);
    }

    [Fact]
    public async Task AbsurdDeadline_OnTheInProcessPath_IsClampedToo_AndNeverThrows()
    {
        var handler = new ChangePasswordHandler();
        var inProcess = InProcessFixture.With(handler, TestData.Definition());
        _inProcess = inProcess.Transport;
        _pipeline = inProcess.Pipeline;

        var response = await Dispatcher().InvokeAsync(TestData.Request(DateTime.MaxValue),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EveryTransportFault_EndsAsTimeoutOrNotAvailable_NeverAsASecondDelivery()
    {
        Exception[] faults =
        [
            NewTimeoutException(), new CommandNotRoutableException(), new DistributedOperationFailedException("x"),
            new OperationCanceledException()
        ];
        foreach (var fault in faults)
        {
            Fake.ClearRecordedCalls(_eventHub);
            _time.Advance(TimeSpan.FromMilliseconds(1));
            AnnounceDefaultHandler(); // a returned send expired it
            BusThrows(fault);

            var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

            Assert.False(response.Success);
            Assert.Equal(1, Sends()); // one delivery attempt, no publisher-side resend
        }
    }

    [Fact]
    public async Task TransportFault_IsNotRetriedByTheDispatcher_EvenWhenTheDeadlineIsFarAway()
    {
        BusThrows(new CommandNotRoutableException());

        var first = await Dispatcher().InvokeAsync(Request(deadlineSeconds: 250), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, first.Error!.Code);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task NonPositiveOptions_FallBackToSafeValues()
    {
        _options.MaxTimeout = TimeSpan.Zero;
        _options.ResponseGrace = TimeSpan.FromSeconds(-5);
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request() with { DeadlineUtc = DateTime.MaxValue },
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(10), SentOptions().Timeout);
    }

    [Fact]
    public async Task BusFault_IsHandlerFailed_WithCorrelationId_AndNoExceptionDetailsOnTheWire()
    {
        var correlation = Guid.NewGuid();
        BusThrows(new DistributedOperationFailedException("secret connection string"));

        var response = await Dispatcher().InvokeAsync(Request() with { CorrelationId = correlation },
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(correlation.ToString(), response.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(response, Wire));
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task CancellationOfTheCaller_Surfaces()
    {
        using var cts = new CancellationTokenSource();
        BusThrows(new OperationCanceledException(cts.Token));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dispatcher().InvokeAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task CancellationNotFromTheCaller_IsHandlerFailed_NotAnException()
    {
        BusThrows(new OperationCanceledException()); // e.g. the bus is stopping

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
    }

    [Fact]
    public async Task ResolverFailure_IsHandlerFailed_NeverAnException()
    {
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Throws<InvalidOperationException>();

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task NullRequest_IsAnArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Dispatcher().InvokeAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeclaringElement_IsResolvedBeforeTheFilters_TheQueueAndTheSentRequest()
    {
        const string declaring = "System.Identity/Principal.ChangePassword-1";
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).Returns(declaring);
        string? seenByFilter = null;
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Authorization, (ctx, next) =>
        {
            seenByFilter = ctx.Request.MethodId;
            return next(ctx.Request);
        }));
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        await Dispatcher().InvokeAsync(Request(methodId: "System.Identity/User.ChangePassword-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(declaring, seenByFilter);
        Assert.Equal(declaring, SentRequest().MethodId);
        Assert.Equal(CkMethodQueueNames.For(declaring), (string)Fake.GetCalls(_eventHub).Single().Arguments[0]!);
    }

    [Fact]
    public async Task FilterRefusal_AnswersWithoutSending()
    {
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Authorization, (ctx, _) =>
            Task.FromResult(CkMethodResponse.Failed(ctx.Request.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodForbidden)))));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task OneFilter_RunsOncePerCall_ForTheBusAndForTheInProcessTransport()
    {
        var seen = new List<string>();
        var filter = new LambdaFilter(CkMethodInvocationFilterOrder.Audit, async (ctx, next) =>
        {
            seen.Add(ctx.Transport);
            return await next(ctx.Request);
        });
        _filters.Add(filter);
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, filter);
        _inProcess = inProcess.Transport;
        _pipeline = inProcess.Pipeline; // the dispatcher adds no pipeline of its own: it shares the one of the transport
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "bus-handler"));
        var dispatcher = Dispatcher();

        var local = await dispatcher.InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);
        var remote = await dispatcher.InvokeAsync(
            TestData.Request(inProcess.InSeconds(15)) with { MethodId = "System.Identity/User.Other-1" },
            TestContext.Current.CancellationToken);

        Assert.True(local.Success);
        Assert.True(remote.Success);
        Assert.Equal([CkMethodTransportNames.InProcess, CkMethodTransportNames.Bus], seen);
        Assert.Equal(1, Sends()); // the in-process call never touched the bus
    }

    [Fact]
    public async Task Facts_AnInnerFilterSets_TheOuterFilterReadsAfterNext_ForTheBusSend()
    {
        // AB#7816: the dispatcher's bus send runs as the terminal step of the shared pipeline
        Guid? seenByAudit = null;
        var first = Guid.NewGuid();
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Audit, async (ctx, next) =>
        {
            var response = await next(ctx.Request);
            seenByAudit = ctx.Facts.ReplayOf;
            return response;
        }));
        _filters.Add(new LambdaFilter(CkMethodInvocationFilterOrder.Idempotency, async (ctx, next) =>
        {
            ctx.Facts.ReplayOf = first;
            return await next(ctx.Request);
        }));
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "bus-handler"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(first, seenByAudit);
    }

    [Fact]
    public async Task InProcessHandler_IsPreferred_BeforeAnyRegistryLookupOrBusSend()
    {
        var handler = new ChangePasswordHandler();
        var inProcess = InProcessFixture.With(handler, TestData.Definition());
        _inProcess = inProcess.Transport;
        _pipeline = inProcess.Pipeline;

        var response = await Dispatcher().InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task InProcessTransportWithoutThatMethod_FallsBackToTheBus()
    {
        var inProcess = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition());
        _inProcess = inProcess.Transport;
        _pipeline = inProcess.Pipeline;
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "remote"));

        var response = await Dispatcher().InvokeAsync(
            TestData.Request(inProcess.InSeconds(15)) with { MethodId = "System.Identity/User.Other-1" },
            TestContext.Current.CancellationToken);

        Assert.Equal("remote", response.HandlerInstanceId);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task Logs_NeverContainParametersOrTheToken()
    {
        BusThrows(new CommandNotRoutableException());

        await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        var text = string.Join("\n", _logs.GetSnapshot().Select(r => r.Message + r.Exception));
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, text);
    }

    [Fact]
    public async Task BusFailureThatQuotesThePayload_IsLoggedMasked()
    {
        BusThrows(new DistributedOperationFailedException(
            $"send failed for {{\"newPassword\":\"{TestData.Secret}\"}} with token {TestData.Token}"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var text = string.Join("\n", _logs.GetSnapshot().Select(r => r.Message + r.Exception));
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, text);
        Assert.Contains("***", text);
    }

    // ---- hand-over after the last handler went away -----------------------------------------------------------

    [Fact]
    public async Task HandOver_SingleReplicaRestart_TheCallWaitsForTheNewInstance_AndIsSent()
    {
        AfterWarmUp();
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "new"));
        WithoutDefaultHandler(); // the only replica stops (graceful shutdown of a restart or a Recreate rollout)

        var call = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        Assert.Equal(0, Sends());

        _time.Advance(TimeSpan.FromSeconds(2)); // the new instance starts and announces itself inside the hand-over
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "restarted", MethodIds = [RemoteMethod] });
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, Sends());
        // the wait used up two seconds of the 15 s deadline
        Assert.Equal(TimeSpan.FromSeconds(13) + _options.ResponseGrace, SentOptions().Timeout);
    }

    [Fact]
    public async Task HandOver_HandlerCrashed_TheExpiryStartsTheHandOver_AndANewInstanceIsWaitedFor()
    {
        AfterWarmUp();
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "new"));
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "device", InstanceId = "crashed", MethodIds = [UnannouncedMethod], TtlSeconds = 45 });
        _time.Advance(TimeSpan.FromSeconds(46)); // no withdrawal: the entry expired one second ago

        var call = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "device", InstanceId = "replacement", MethodIds = [UnannouncedMethod] });

        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(response.Success);
        Assert.Equal(1, Sends());
    }

    [Fact]
    public async Task HandOver_IsCountedFromTheWithdrawal_NotFromTheCall()
    {
        AfterWarmUp();
        WithoutDefaultHandler();
        _time.Advance(_options.HandlerHandOver - TimeSpan.FromSeconds(1));

        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 60), TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1)); // the rest of the window, not a whole hand-over
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal("no-live-handler", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task HandOver_EndsAtTheRequestDeadline()
    {
        _options.HandlerHandOver = TimeSpan.FromSeconds(30);
        AfterWarmUp();
        WithoutDefaultHandler();

        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 2), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(2));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task HandOver_IsCappedAtItsMaximum()
    {
        _options.HandlerHandOver = TimeSpan.FromHours(1);
        AfterWarmUp();
        WithoutDefaultHandler();

        var call = Dispatcher().InvokeAsync(Request(deadlineSeconds: 300), TestContext.Current.CancellationToken);
        _time.Advance(CkMethodDispatcherOptions.MaxHandlerHandOver);
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task HandOverZeroOrNegative_RefusesAtOnce_AsBefore(int seconds)
    {
        _options.HandlerHandOver = TimeSpan.FromSeconds(seconds);
        AfterWarmUp();
        WithoutDefaultHandler();

        var call = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(call.IsCompleted);
        Assert.Equal("no-live-handler", (await call).Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task HandOver_CancellationOfTheCaller_Surfaces_AndFreesItsPlace()
    {
        AfterWarmUp();
        WithoutDefaultHandler();
        using var cts = new CancellationTokenSource();
        var call = Dispatcher().InvokeAsync(Request(), cts.Token);
        Assert.Equal(1, _registry.WaitingCalls);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, _registry.WaitingCalls);
        Assert.Equal(0, Sends());
    }

    [Fact]
    public async Task HandOver_WaitingCallsAreBounded_TheCallBeyondTheLimitIsRefusedAtOnce()
    {
        _options.MaxWaitingCalls = 2;
        AfterWarmUp();
        WithoutDefaultHandler();
        var dispatcher = Dispatcher();

        var first = dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        var second = dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        var third = dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.True(third.IsCompleted);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, (await third).Error!.Code);
        Assert.Equal(2, _registry.WaitingCalls);

        AfterTheHandOver();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, _registry.WaitingCalls); // every place is given back
        Assert.Contains(_logs.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains("too many calls wait"));
    }

    [Fact]
    public async Task MaxWaitingCallsZero_NoCallWaitsInTheHandOver_TheWarmUpStillWaits()
    {
        _options.MaxWaitingCalls = 0;
        WithoutDefaultHandler();

        var handOver = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        var warmUp = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);

        Assert.True(handOver.IsCompleted);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, (await handOver).Error!.Code);
        Assert.False(warmUp.IsCompleted); // the warm-up after a start is not bounded by the option
        AfterWarmUp();
        await warmUp.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandOver_ALateWithdrawalOfAnEndedEntry_DoesNotStartANewWindow()
    {
        AfterWarmUp();
        WithoutDefaultHandler();
        _time.Advance(TimeSpan.FromMinutes(5));

        WithoutDefaultHandler(); // delivered again, long after the instance stopped
        var call = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(call.IsCompleted);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, (await call).Error!.Code);
    }

    [Fact]
    public async Task BrokerReturn_OfARowRestoredFromTheSnapshot_GetsTheHandOverToo()
    {
        WithoutDefaultHandler();
        _registry.Restore([
            new CkMethodHandlerSnapshotRecord
            {
                MethodKey = CkMethodHandlerRegistry.Normalize(UnannouncedMethod), InstanceId = "restored",
                MethodId = UnannouncedMethod, ServiceName = "device", ContractVersion = 1, LastSeenUtc = Now,
                ExpiresUtc = Now.AddMinutes(10), Tenants = ["*"]
            }
        ]);
        AfterWarmUp();
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._))
            .Throws(new CommandNotRoutableException()).Once()
            .Then.Returns(CkMethodResponse.Succeeded(Guid.Empty, "restored"));

        var first = await Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);
        Assert.Equal("no-live-handler", first.Error!.Parameters[CkMethodErrorParameters.Reason]);
        await WaitForDiscoveryRequestsAsync(1);

        var second = Dispatcher().InvokeAsync(Request(methodId: UnannouncedMethod), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted); // the warm-up is over: this is the hand-over
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "device", InstanceId = "restored", MethodIds = [UnannouncedMethod] });

        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Success);
    }

    [Fact]
    public async Task BrokerReturn_AHandlerThatAnswersButStaysUnbound_IsRefusedAtOnceAfterItsSecondReturn_WithoutFurtherRequests()
    {
        AfterWarmUp();
        BusThrows(new CommandNotRoutableException()); // the process answers, its method queue stays unbound
        var dispatcher = Dispatcher();

        await dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        await WaitForDiscoveryRequestsAsync(1);
        var waiting = dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        AnnounceDefaultHandler(); // answer to the discovery request
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("no-live-handler", second.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(2, Sends()); // sent once more, returned again

        _time.Advance(CkMethodHandlerDiscoveryRequester.MinInterval);
        var third = dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(third.IsCompleted); // repeated return: no hand-over wait
        Assert.Equal(2, Sends());
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, DiscoveryRequests()); // and no further request
    }

    [Fact]
    public async Task BrokerReturn_AHangingDiscoveryPublish_IsAbandoned_SoALaterReturnCanAskAgain()
    {
        AfterWarmUp();
        var hanging = new TaskCompletionSource();
        A.CallTo(() => _eventHub.PublishAsync(A<CkMethodHandlerDiscoveryRequested>._, A<CancellationToken?>._))
            .Returns(hanging.Task);
        var requester = new CkMethodHandlerDiscoveryRequester(_eventHub, _time,
            NullLogger<CkMethodHandlerDiscoveryRequester>.Instance);

        Assert.True(requester.Request());
        await WaitForDiscoveryRequestsAsync(1);
        _time.Advance(CkMethodHandlerDiscoveryRequester.MinInterval);
        Assert.False(requester.Request()); // still in flight

        _time.Advance(CkMethodHandlerDiscoveryRequester.PublishTimeout);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var again = false;
        while (!again && DateTime.UtcNow < until)
        {
            again = requester.Request();
            if (!again)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(again);
        hanging.TrySetResult();
    }

    [Fact]
    public async Task HandOver_ManyWaitingCalls_AllEndWithTheAnnouncement_AndEachIsSentOnce()
    {
        AfterWarmUp();
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "new"));
        WithoutDefaultHandler();
        var dispatcher = Dispatcher();

        var calls = Enumerable.Range(0, 50)
            .Select(_ => dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken)).ToList();
        Assert.Equal(50, _registry.WaitingCalls);
        _registry.OnAnnounced(new CkMethodHandlerAnnounced(Guid.NewGuid(), Now)
            { ServiceName = "identity", InstanceId = "restarted", MethodIds = [RemoteMethod] });
        var responses = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(responses, r => Assert.True(r.Success));
        Assert.Equal(50, Sends());
        Assert.Equal(0, _registry.WaitingCalls);
    }

    // ---- discovery request after a returned send ----------------------------------------------------------------

    [Fact]
    public async Task BrokerReturn_PublishesADiscoveryRequest_AndALiveHandlerThatAnswersIsCalledWithoutWaitingForItsHeartbeat()
    {
        AfterWarmUp();
        // the handler is live but its queue is not bound for a moment (it re-binds after a broker reconnect)
        A.CallTo(() => _eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._))
            .Throws(new CommandNotRoutableException()).Once()
            .Then.Returns(CkMethodResponse.Succeeded(Guid.Empty, DefaultInstance));

        var first = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal("no-live-handler", first.Error!.Parameters[CkMethodErrorParameters.Reason]);
        await WaitForDiscoveryRequestsAsync(1);

        var second = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted); // the expired entry is in its hand-over: the call waits for the answer
        AnnounceDefaultHandler(); // the handler's answer to the discovery request, no time passed
        var response = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(2, Sends()); // the returned call is never sent again
    }

    [Fact]
    public async Task BrokerReturn_DiscoveryRequestsAreThrottled_AndRepeatedWhileCallsWaitInTheHandOver()
    {
        AfterWarmUp();
        BusThrows(new CommandNotRoutableException());
        var dispatcher = Dispatcher();

        await dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        await WaitForDiscoveryRequestsAsync(1);

        // a burst of calls in the same second: no further request
        var waiting = Enumerable.Range(0, 20)
            .Select(_ => dispatcher.InvokeAsync(Request(deadlineSeconds: 60), TestContext.Current.CancellationToken)).ToList();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, DiscoveryRequests());

        // about one second later a waiting call asks again: a handler that was still re-binding answers this one
        // (advanced in steps: the waiting calls re-arm their timers on other threads)
        var advanced = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(250);
        while (DiscoveryRequests() < 2 && advanced < _options.HandlerHandOver - step)
        {
            _time.Advance(step);
            advanced += step;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.InRange(DiscoveryRequests(), 2, 3);
        Assert.True(advanced >= CkMethodHandlerDiscoveryRequester.MinInterval, $"asked again after {advanced}");

        var all = Task.WhenAll(waiting);
        await AdvanceUntilCompletedAsync(all, step);
        var responses = await all;
        Assert.All(responses, r => Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, r.Error!.Code));
        Assert.Equal(1, Sends());
        // at most one request per second of the hand-over
        Assert.InRange(DiscoveryRequests(), 2, 1 + (int)Math.Ceiling(_options.HandlerHandOver / CkMethodHandlerDiscoveryRequester.MinInterval));

        // after the hand-over a call neither waits nor asks again
        var before = DiscoveryRequests();
        _time.Advance(CkMethodHandlerDiscoveryRequester.MinInterval);
        Assert.True(dispatcher.InvokeAsync(Request(), TestContext.Current.CancellationToken).IsCompleted);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(before, DiscoveryRequests());
    }

    [Fact]
    public async Task Withdrawal_PublishesNoDiscoveryRequest()
    {
        AfterWarmUp();
        WithoutDefaultHandler();

        var call = Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);
        AfterTheHandOver();
        await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, DiscoveryRequests()); // a stopped instance announces its successor itself
    }

    [Fact]
    public async Task BrokerReturn_AFailingDiscoveryPublish_DoesNotChangeTheAnswer()
    {
        AfterWarmUp();
        BusThrows(new CommandNotRoutableException());
        A.CallTo(() => _eventHub.PublishAsync(A<CkMethodHandlerDiscoveryRequested>._, A<CancellationToken?>._))
            .ThrowsAsync(new InvalidOperationException("broker gone"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal("no-live-handler", response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!_logs.GetSnapshot().Any(r => r.Message.Contains("discovery request failed")) && DateTime.UtcNow < until)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var log = Assert.Single(_logs.GetSnapshot(), r => r.Message.Contains("discovery request failed"));
        Assert.DoesNotContain("broker gone", log.Message); // the type only
    }


    private sealed class LambdaFilter(int order,
        Func<CkMethodInvocationContext, CkMethodInvocationDelegate, Task<CkMethodResponse>> run) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            run(context, next);
    }
}
