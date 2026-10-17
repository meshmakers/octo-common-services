using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

public class InvocationPipelineTests
{
    private static ChangePasswordHandler Handler() => new();

    [Fact]
    public async Task SameFilter_RunsForTheBusAndTheInProcessTransport_WithTheirTransportName()
    {
        var filter = new RecordingFilter(CkMethodInvocationFilterOrder.Audit);
        var busHost = HostFixture.With(Handler(), TestData.Definition());
        var inProcess = InProcessFixture.With(Handler(), TestData.Definition(), TestData.MethodId, filter);
        var request = TestData.Request(inProcess.InSeconds(15));

        // what the dispatcher of F3.3 does for a remote handler: the same pipeline type around the bus send
        var bus = await inProcess.Pipeline.ExecuteAsync(CkMethodTransportNames.Bus, request,
            r => busHost.BusConsumer.HandleAsync(r), TestContext.Current.CancellationToken);
        var local = await inProcess.Transport.InvokeAsync(
            TestData.Request(inProcess.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.True(bus.Success && local.Success);
        Assert.Equal([CkMethodTransportNames.Bus, CkMethodTransportNames.InProcess], filter.Transports);
    }

    [Fact]
    public async Task Filters_RunInOrder_OuterFirst()
    {
        var trace = new List<string>();
        var late = new RecordingFilter(CkMethodInvocationFilterOrder.Idempotency, trace, "idempotency");
        var early = new RecordingFilter(CkMethodInvocationFilterOrder.Authorization, trace, "authorization");
        var host = InProcessFixture.With(Handler(), TestData.Definition(), TestData.MethodId, late, early);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(["authorization>", "idempotency>", "idempotency<", "authorization<"], trace);
    }

    [Fact]
    public async Task RefusingFilter_AnswersItsEnvelope_AndTheHandlerNeverRuns()
    {
        var handler = Handler();
        var refuse = new RecordingFilter(CkMethodInvocationFilterOrder.Authorization)
        {
            Answer = r => CkMethodResponse.Failed(r.InvocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
                    (CkMethodErrorParameters.MethodId, r.MethodId), (CkMethodErrorParameters.Reason, "no-role")))
        };
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, refuse);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FilterMayContinueWithAModifiedRequest()
    {
        var handler = Handler();
        var key = new RecordingFilter(CkMethodInvocationFilterOrder.Idempotency)
        {
            Rewrite = r => r with { IdempotencyKey = "key-1" }
        };
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, key);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal("key-1", handler.LastContext!.IdempotencyKey);
    }

    [Fact]
    public async Task ThrowingFilter_IsAnsweredAsHandlerFailed_WithoutTheTokenInTheLog()
    {
        var broken = new RecordingFilter(CkMethodInvocationFilterOrder.Audit)
        {
            Throw = () => new InvalidOperationException("audit store down, token " + TestData.Token)
        };
        var handler = Handler();
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, broken);
        var correlation = Guid.NewGuid();

        var response = await host.Transport.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { CorrelationId = correlation },
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(correlation.ToString(), response.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(TestData.Token, host.AllLogText());
    }

    [Fact]
    public async Task CallerCancellation_PassesThroughTheFilters()
    {
        var filter = new RecordingFilter(CkMethodInvocationFilterOrder.Audit)
        {
            Throw = () => new OperationCanceledException()
        };
        var host = InProcessFixture.With(Handler(), TestData.Definition(), TestData.MethodId, filter);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), cts.Token));
    }

    private sealed class RecordingFilter(int order, List<string>? trace = null, string name = "filter")
        : ICkMethodInvocationFilter
    {
        public List<string> Transports { get; } = [];

        public Func<CkMethodRequest, CkMethodResponse>? Answer { get; init; }

        public Func<CkMethodRequest, CkMethodRequest>? Rewrite { get; init; }

        public Func<Exception>? Throw { get; init; }

        public int Order => order;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context,
            CkMethodInvocationDelegate next)
        {
            Transports.Add(context.Transport);
            trace?.Add(name + ">");
            if (Throw != null)
            {
                throw Throw();
            }

            if (Answer != null)
            {
                return Answer(context.Request);
            }

            var response = await next(Rewrite?.Invoke(context.Request) ?? context.Request);
            trace?.Add(name + "<");
            return response;
        }
    }
}

public class InvocationFilterOrderTests
{
    [Fact]
    public void Bands_ArePinned()
    {
        Assert.Equal(100, CkMethodInvocationFilterOrder.Audit);
        Assert.Equal(200, CkMethodInvocationFilterOrder.Authorization);
        Assert.Equal(300, CkMethodInvocationFilterOrder.Idempotency);
    }

    private sealed class Step(int order, List<string> trace, string name, Func<CkMethodRequest, CkMethodResponse>? answer = null)
        : ICkMethodInvocationFilter
    {
        public int Order => order;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            trace.Add($"{name}:{context.Transport}>");
            var response = answer != null ? answer(context.Request) : await next(context.Request);
            trace.Add($"{name}<{(response.Success ? "ok" : response.Error!.Code)}");
            return response;
        }
    }

    private static CkMethodResponse Forbidden(CkMethodRequest r) =>
        CkMethodResponse.Failed(r.InvocationId, string.Empty, CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
            (CkMethodErrorParameters.MethodId, r.MethodId), (CkMethodErrorParameters.Reason, "no-role")));

    private static (List<string> Trace, ICkMethodInvocationFilter[] Filters) Chain(bool refuse)
    {
        var trace = new List<string>();
        // registered in a scrambled order on purpose
        return (trace,
        [
            new Step(CkMethodInvocationFilterOrder.Idempotency, trace, "idempotency"),
            new Step(CkMethodInvocationFilterOrder.Audit, trace, "audit"),
            new Step(CkMethodInvocationFilterOrder.Authorization, trace, "authorization", refuse ? Forbidden : null)
        ]);
    }

    [Fact]
    public async Task RefusingAuthorization_StillReachesAudit_WithTheFailedOutcome()
    {
        var (trace, filters) = Chain(refuse: true);
        var handler = new ChangePasswordHandler();
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, filters);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(["audit:in-process>", "authorization:in-process>", "authorization<METHOD_FORBIDDEN",
            "audit<METHOD_FORBIDDEN"], trace);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RefusingAuthorization_NeverReachesIdempotency()
    {
        var (trace, filters) = Chain(refuse: true);
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, filters);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(trace, t => t.StartsWith("idempotency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrderIsTheSame_ForBusAndInProcess()
    {
        var (trace, filters) = Chain(refuse: false);
        var busHost = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, filters);

        await host.Pipeline.ExecuteAsync(CkMethodTransportNames.Bus, TestData.Request(host.InSeconds(15)),
            r => busHost.BusConsumer.HandleAsync(r), TestContext.Current.CancellationToken);
        var bus = trace.ToList();
        trace.Clear();
        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(["audit", "authorization", "idempotency", "idempotency", "authorization", "audit"],
            bus.Select(t => t.Split(':', '<', '>')[0]));
        Assert.Equal(bus.Select(t => t.Replace(":bus", ":x")), trace.Select(t => t.Replace(":in-process", ":x")));
    }
}
