using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     Duplicate deliveries that the broker does not produce in the forced-failure measurement (AB#7936): a caller that
///     sends the same invocation id twice, and a consumer that is too late with its acknowledgement. They pin the
///     rows of the duplicate-classes table in <c>methodConsistency.md</c> (R4, "Limits: what is not detected").
/// </summary>
public sealed partial class CkMethodDispatcherBusTests
{
    [Fact]
    public async Task SameInvocationIdSentTwice_ToOneInstance_RunsOnce_TheSecondIsOutcomeUnknown()
    {
        var (_, probe) = await StartHandlerHostAsync("HandlerA");
        var (_, dispatcher, _) = await StartGatewayAsync();
        var request = NewRequest();

        var first = await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);
        var second = await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(first.HandlerInstanceId, second.HandlerInstanceId); // refused by the instance that ran it (id memory)
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task SameInvocationIdSentTwice_ToTwoInstances_RunsOnEachInstance_NobodyRecognizesIt()
    {
        // The documented limit: the memory of seen ids is per instance and the second message carries no broker flag.
        // The queue is shared, so the broker alternates between the instances.
        var (_, probeA) = await StartHandlerHostAsync("HandlerA");
        var (_, probeB) = await StartHandlerHostAsync("HandlerA");
        var (_, dispatcher, _) = await StartGatewayAsync();
        var request = NewRequest();

        var first = await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);
        var second = await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.True(first.Success);
        Assert.True(second.Success, "the duplicate on the other instance is not recognized (documented limit)");
        Assert.NotEqual(first.HandlerInstanceId, second.HandlerInstanceId);
        Assert.Equal(2, probeA.Calls + probeB.Calls);
        Assert.Equal(1, probeA.Calls);
        Assert.Equal(1, probeB.Calls);
    }

    [Fact]
    public async Task AckAfterTheConsumerTimeout_TheBrokerRequeuesFlagged_TheOtherInstanceRefuses_TheLateAnswerChangesNothing()
    {
        // A handler holds its delivery longer than the broker's consumer timeout: the broker closes the consumer's channel
        // and requeues the unacknowledged message flagged as redelivered. The first execution is still running.
        _broker = await RabbitMqFixture.StartBrokerWithConsumerTimeoutAsync(TimeSpan.FromSeconds(5));
        var (_, probeA) = await StartHandlerHostAsync("HandlerA");
        var (_, probeB) = await StartHandlerHostAsync("HandlerB");
        var (_, dispatcher, _) = await StartGatewayAsync();

        var call = dispatcher.InvokeAsync(NewRequest(target: "gate", timeout: TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        await Task.WhenAny(probeA.Entered.Task, probeB.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        var (busy, other) = probeA.Entered.Task.IsCompleted ? (probeA, probeB) : (probeB, probeA);

        var response = await call.WaitAsync(TimeSpan.FromSeconds(150), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
        Assert.Equal(1, busy.Calls);
        Assert.Equal(0, other.Calls); // the flagged redelivery was refused, not run

        busy.Release.TrySetResult(); // the first execution ends late; its ack and answer cannot change the outcome
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(1, probeA.Calls + probeB.Calls);

        // what the consumer does afterwards is pinned in the assertions below
        var before = (A: probeA.Calls, B: probeB.Calls);
        for (var i = 0; i < 20; i++)
        {
            var followUp = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
            Assert.True(followUp.Success, System.Text.Json.JsonSerializer.Serialize(followUp, Wire));
        }

        // Observed: the broker closed the channel of the instance that held the delivery too long, and that instance does
        // not consume again (AB#7936 finding; the other instance serves every follow-up). Asserted here is only what must
        // hold either way: the callers are served, and nothing ran twice.
        var servedByOther = ReferenceEquals(busy, probeA) ? probeB.Calls - before.B : probeA.Calls - before.A;
        Assert.True(servedByOther > 0);
        Assert.Equal(before.A + before.B + 20, probeA.Calls + probeB.Calls);
    }
}
