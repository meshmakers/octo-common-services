using System.Diagnostics;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     The hand-over of the dispatcher against the real broker: a call that arrives right after the only handler instance
///     went away waits a bounded time for a new announcement (single-replica restart), and a send the broker returns
///     while a handler re-binds its queue (broker connection lost and restored) asks the handlers to announce themselves
///     again, so calls succeed long before the handler's next heartbeat.
/// </summary>
public sealed partial class CkMethodDispatcherBusTests
{
    /// <summary>Generous for shared build agents; a quiet machine needs a fraction of it.</summary>
    private static readonly TimeSpan HandOverForTests = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HandOver_SingleReplicaRestart_TheCallMadeWhileNoInstanceRuns_ReachesTheNewInstance()
    {
        var (handler, _) = await StartHandlerHostAsync();
        var (gateway, dispatcher, _) = await StartGatewayAsync(handOver: HandOverForTests);
        await HandlerReadiness.WaitForLiveHandlersAsync(gateway.Provider, MethodId, 1);

        await handler.DisposeAsync(); // the only replica stops: withdrawal, endpoints removed, bus stopped
        await WaitUntilAsync(() => gateway.Provider.GetRequiredService<ICkMethodHandlerRegistry>().GetLiveHandlers(MethodId)
            .Count == 0, HandlerReadiness.Timeout);

        var sw = Stopwatch.StartNew();
        var call = dispatcher.InvokeAsync(NewRequest(timeout: TimeSpan.FromSeconds(20)), TestContext.Current.CancellationToken);
        await StartHandlerHostAsync(); // the new instance announces itself on start
        var response = await call.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(response.Success, HandlerReadiness.DescribeFailure(response, gateway.Provider));
        Assert.True(sw.Elapsed < HandOverForTests, $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task HandOver_NoNewInstance_TheCallIsRefusedWhenTheHandOverEnds()
    {
        var handOver = TimeSpan.FromSeconds(1);
        var (handler, _) = await StartHandlerHostAsync();
        var (gateway, dispatcher, _) = await StartGatewayAsync(handOver: handOver);
        await HandlerReadiness.WaitForLiveHandlersAsync(gateway.Provider, MethodId, 1);

        await handler.DisposeAsync();
        await WaitUntilAsync(() => gateway.Provider.GetRequiredService<ICkMethodHandlerRegistry>().GetLiveHandlers(MethodId)
            .Count == 0, HandlerReadiness.Timeout);
        var sw = Stopwatch.StartNew();
        var response = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal("no-live-handler", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.True(sw.Elapsed < handOver + TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");

        // after the hand-over the refusal is immediate again
        sw.Restart();
        var again = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        sw.Stop();
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, again.Error!.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task BrokerReturn_HandlerConnectionLostAndRestored_CallsSucceedBeforeTheNextHeartbeat()
    {
        // The heartbeat is far away, so only the discovery request can bring the handler back into the registry in time.
        var heartbeat = TimeSpan.FromMinutes(2);
        var (gateway, dispatcher, _) = await StartGatewayAsync(handOver: HandOverForTests);
        var before = (await rabbitMq.RabbitMqCtlAsync("list_connections", "pid")).ToHashSet();
        await StartHandlerHostAsync(heartbeat: heartbeat);
        var handlerConnections = (await rabbitMq.RabbitMqCtlAsync("list_connections", "pid")).Except(before).ToList();
        Assert.NotEmpty(handlerConnections);
        await HandlerReadiness.WaitForLiveHandlersAsync(gateway.Provider, MethodId, 1);
        var first = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.True(first.Success, HandlerReadiness.DescribeFailure(first, gateway.Provider));

        // The handler loses its broker connection: its auto-delete method queue goes away until the transport
        // reconnects and declares it again, while the gateway's registry still lists the handler as live.
        var lost = Stopwatch.StartNew();
        foreach (var pid in handlerConnections)
        {
            await rabbitMq.RabbitMqCtlAsync("close_connection", pid, "hand-over-test");
        }

        var refused = 0;
        CkMethodResponse? response = null;
        while (lost.Elapsed < heartbeat / 2)
        {
            response = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
            if (response.Success)
            {
                break;
            }

            Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
            refused++;
        }

        lost.Stop();
        Assert.True(response is { Success: true },
            $"no successful call within {lost.Elapsed} ({refused} refused): " +
            (response == null ? "no call" : HandlerReadiness.DescribeFailure(response, gateway.Provider)));
        Assert.True(lost.Elapsed < heartbeat / 2, $"took {lost.Elapsed}");

        // Every refusal here was a returned send (the registry listed the handler), and a returned send asks the handlers
        // to announce themselves again. A first call that succeeds at once (fast reconnect) had nothing to ask.
        var discovery = gateway.Provider.GetRequiredService<CkMethodHandlerDiscoveryRequester>();
        if (refused > 0)
        {
            Assert.True(discovery.Published > 0, "a send was refused but no discovery request was published");
        }

        TestContext.Current.SendDiagnosticMessage(
            $"handler connection lost: first success after {lost.Elapsed}, {refused} refused, " +
            $"{discovery.Published} discovery request(s)");
    }
}
