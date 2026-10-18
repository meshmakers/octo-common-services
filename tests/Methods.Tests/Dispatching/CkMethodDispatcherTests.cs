using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
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

    public CkMethodDispatcherTests()
    {
        _registry = new CkMethodHandlerRegistry(_time);
        A.CallTo(() => _resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private CkMethodRequest Request(int deadlineSeconds = 15, string methodId = RemoteMethod) =>
        TestData.Request(Now.AddSeconds(deadlineSeconds)) with { MethodId = methodId };

    private CkMethodDispatcher Dispatcher()
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        _pipeline ??= new CkMethodInvocationPipeline(_filters, loggers.CreateLogger<CkMethodInvocationPipeline>());
        var services = new ServiceCollection();
        if (_inProcess != null)
        {
            services.AddSingleton<ICkInProcessMethodTransport>(_inProcess);
        }

        return new CkMethodDispatcher(_resolver, _pipeline, _registry, _eventHub, services.BuildServiceProvider(),
            Options.Create(_options), _time, loggers.CreateLogger<CkMethodDispatcher>());
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
    public async Task EmptyRegistry_DoesNotRefuse_TheBrokerDecides()
    {
        BusReturns(CkMethodResponse.Succeeded(Guid.Empty, "h"));

        var response = await Dispatcher().InvokeAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task HandlersOnlyForOtherTenants_AreNotAvailable_WithoutSending()
    {
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

    private sealed class LambdaFilter(int order,
        Func<CkMethodInvocationContext, CkMethodInvocationDelegate, Task<CkMethodResponse>> run) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            run(context, next);
    }
}
