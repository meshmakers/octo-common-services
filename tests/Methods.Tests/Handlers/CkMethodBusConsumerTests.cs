using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

public class CkMethodBusConsumerTests
{
    [Fact]
    public async Task Handler_RespondsExactlyOnce_WithTheInvokerResponse()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var responses = new List<object>();

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)), TestDelivery.First, r =>
        {
            responses.Add(r);
            return Task.CompletedTask;
        });

        var response = Assert.IsType<CkMethodResponse>(Assert.Single(responses));
        Assert.True(response.Success);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SameInvocationIdTwice_ExecutesOnce_SecondIsOutcomeUnknown()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var first = await host.BusConsumer.HandleAsync(request);
        var second = await host.BusConsumer.HandleAsync(request);

        Assert.True(first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(TestData.MethodId, second.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FailingResponseChannel_IsSwallowed_SoTheBusDoesNotRetryTheMethod()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)), TestDelivery.First,
            _ => throw new InvalidOperationException("broker gone"));

        Assert.Equal(1, handler.Calls);
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task SeenInvocations_AreForgottenAfterTheirRetention()
    {
        var host = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        for (var i = 0; i < 256; i++)
        {
            await host.BusConsumer.HandleAsync(TestData.Request(host.InSeconds(1)));
        }

        host.Time.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 256; i++)
        {
            await host.BusConsumer.HandleAsync(TestData.Request(host.InSeconds(1)));
        }

        Assert.True(host.BusConsumer.SeenInvocationCount < 512);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PossibleDuplicateDelivery_IsNotExecuted_AndAnsweredOutcomeUnknown(bool brokerRedelivery)
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var responses = new List<object>();

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)),
            brokerRedelivery ? TestDelivery.Redelivered : TestDelivery.Retried, r =>
            {
                responses.Add(r);
                return Task.CompletedTask;
            });

        var response = Assert.IsType<CkMethodResponse>(Assert.Single(responses));
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(0, handler.Calls);
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains("possible duplicate"));
    }

    [Fact]
    public async Task RefusedRedelivery_IsRemembered_SoALaterDeliveryOfTheSameInvocationIsRefusedToo()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var redelivered = await host.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);
        var again = await host.BusConsumer.HandleAsync(request, TestDelivery.First);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, redelivered.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, again.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FirstDelivery_IsExecuted_AndTheInvocationIdStaysSecondLine()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var first = await host.BusConsumer.HandleAsync(request, TestDelivery.First);
        var second = await host.BusConsumer.HandleAsync(request, TestDelivery.First);

        Assert.True(first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtremeDeadline_NeverThrows_InTheSeenBookkeeping(bool maxValue)
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var deadline = DateTime.SpecifyKind(maxValue ? DateTime.MaxValue : DateTime.MinValue, DateTimeKind.Utc);
        var request = TestData.Request(deadline);

        var first = await host.BusConsumer.HandleAsync(request);
        var second = await host.BusConsumer.HandleAsync(request);

        // MaxValue: served, then refused as a repeat; MinValue: both are late
        Assert.Equal(maxValue, first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(maxValue ? 1 : 0, handler.Calls);
    }

    [Fact]
    public async Task SeenRetention_IsCappedAtOneHour_EvenForAnEndlessDeadline()
    {
        var host = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        await host.BusConsumer.HandleAsync(TestData.Request(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));

        host.Time.Advance(TimeSpan.FromMinutes(61)); // keepUntil = min(deadline, now + 1 h) + 5 min, capped at now + 1 h
        for (var i = 0; i < 256; i++)
        {
            await host.BusConsumer.HandleAsync(TestData.Request(host.InSeconds(1)));
        }

        Assert.True(host.BusConsumer.SeenInvocationCount <= 256);
    }

    [Fact]
    public async Task SeenRetention_OfARegularDeadline_IsTheDeadlinePlusFiveMinutes()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));
        await host.BusConsumer.HandleAsync(request);

        host.Time.Advance(TimeSpan.FromSeconds(15 + 4 * 60)); // inside deadline + 5 min: still remembered
        var repeat = await host.BusConsumer.HandleAsync(request);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, repeat.Error!.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DuplicateLogLine_NamesInvocationTenantAndDeliveryInfo_ButNoParameters()
    {
        var host = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        await host.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);

        var line = Assert.Single(host.Logs.GetSnapshot(), r => r.Message.Contains("possible duplicate")).Message;
        Assert.Contains(request.InvocationId.ToString(), line);
        Assert.Contains("meshtest", line);
        Assert.Contains("redelivered True", line);
        Assert.DoesNotContain(TestData.Secret, host.AllLogText());
        Assert.DoesNotContain(TestData.OldSecret, host.AllLogText());
        Assert.DoesNotContain(TestData.Token, host.AllLogText());
    }

    [Fact]
    public async Task SideEffectThenThrow_RunsOnce_AndAnAlreadySeenRedeliveryDoesNotRunItAgain()
    {
        var handler = new ChangePasswordHandler((_, _) => throw new InvalidOperationException("after the side effect"));
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var first = await host.BusConsumer.HandleAsync(request);
        var redelivery = await host.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, first.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, redelivery.Error!.Code);
        Assert.Equal(1, handler.Calls);
    }

    // ---- duplicate cases a broker cannot produce (AB#7936): simulated deliveries -------------------------------------

    [Fact]
    public async Task SameInvocationId_DeliveredToTwoInstances_RunsOnEachOne_BecauseTheMemoryIsPerInstance()
    {
        var handlerA = new ChangePasswordHandler();
        var handlerB = new ChangePasswordHandler();
        var hostA = HostFixture.With(handlerA, TestData.Definition());
        var hostB = HostFixture.With(handlerB, TestData.Definition());
        var request = TestData.Request(hostA.InSeconds(15));

        // neither delivery carries a broker flag: a caller that re-sends the request unchanged
        var first = await hostA.BusConsumer.HandleAsync(request, TestDelivery.First);
        var second = await hostB.BusConsumer.HandleAsync(request, TestDelivery.First);

        Assert.True(first.Success);
        Assert.True(second.Success); // documented limit (methodConsistency.md, R4): not recognized across instances
        Assert.Equal(1, handlerA.Calls);
        Assert.Equal(1, handlerB.Calls);
    }

    [Fact]
    public async Task SameInvocationId_DeliveredToTwoInstances_WithTheBrokerFlag_RunsOnlyOnTheFirst()
    {
        var handlerA = new ChangePasswordHandler();
        var handlerB = new ChangePasswordHandler();
        var hostA = HostFixture.With(handlerA, TestData.Definition());
        var hostB = HostFixture.With(handlerB, TestData.Definition());
        var request = TestData.Request(hostA.InSeconds(15));

        await hostA.BusConsumer.HandleAsync(request, TestDelivery.First);
        var redelivery = await hostB.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, redelivery.Error!.Code);
        Assert.Equal(1, handlerA.Calls);
        Assert.Equal(0, handlerB.Calls);
    }

    [Fact]
    public async Task AckAfterTheConsumerTimeout_TheFlaggedRedeliveryIsRefused_WhileTheFirstExecutionIsStillRunning()
    {
        // A runs the call and does not finish; the broker's consumer timeout closes A's channel and requeues the
        // message flagged as redelivered; B (or A again) gets it while the first execution still runs.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerA = new ChangePasswordHandler(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new UserChangePasswordResult { Value = "changed" };
        });
        var handlerB = new ChangePasswordHandler();
        var hostA = HostFixture.With(handlerA, TestData.Definition());
        var hostB = HostFixture.With(handlerB, TestData.Definition());
        var request = TestData.Request(hostA.InSeconds(300));

        var running = hostA.BusConsumer.HandleAsync(request, TestDelivery.First);
        await entered.Task;
        var onOtherInstance = await hostB.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);
        var onSameInstance = await hostA.BusConsumer.HandleAsync(request, TestDelivery.Redelivered);
        release.SetResult(); // the late ack: the first execution finishes after the redelivery was handled
        var late = await running;

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, onOtherInstance.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, onSameInstance.Error!.Code);
        Assert.True(late.Success); // the first execution still answers; the caller took the first envelope it got
        Assert.Equal(1, handlerA.Calls);
        Assert.Equal(0, handlerB.Calls);
    }
}
