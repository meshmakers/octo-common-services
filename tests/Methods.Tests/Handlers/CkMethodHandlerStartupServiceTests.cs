using FakeItEasy;
using MassTransit;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>The announcement waits until every method endpoint accepts messages (AB#6891).</summary>
public class CkMethodHandlerStartupServiceTests
{
    private sealed class Rig
    {
        public Rig(TimeSpan? readyTimeout = null)
        {
            Host = HostFixture.With(new ChangePasswordHandler(), TestData.Definition());
            Ready = new TaskCompletionSource<ReceiveEndpointReady>(TaskCreationOptions.RunContinuationsAsynchronously);
            var endpoint = A.Fake<HostReceiveEndpointHandle>();
            A.CallTo(() => endpoint.Ready).Returns(Ready.Task);

            Control = A.Fake<IEventHubControl>();
            A.CallTo(() => Control.RegisterCommandConsumer(A<string>._,
                    A<ExecuteDeliveredCommandHandler<CkMethodRequest>>._, A<CommandConsumerOptions>._))
                .Returns(new EndpointHandle(endpoint));
            EventHub = A.Fake<IDistributionEventHubService>();

            var options = new CkMethodHandlerOptions();
            if (readyTimeout is { } timeout)
            {
                options.EndpointReadyTimeout = timeout;
            }

            Sut = new CkMethodHandlerStartupService(Control, EventHub, Host.Instance, Host.BusConsumer,
                Options.Create(options), new FakeLogger<CkMethodHandlerStartupService>(Host.Logs));
        }

        public HostFixture Host { get; }
        public TaskCompletionSource<ReceiveEndpointReady> Ready { get; }
        public IEventHubControl Control { get; }
        public IDistributionEventHubService EventHub { get; }
        public CkMethodHandlerStartupService Sut { get; }

        public void AssertAnnouncements(int expected) =>
            A.CallTo(EventHub).Where(c => c.Method.Name == nameof(IDistributionEventHubService.PublishAsync))
                .MustHaveHappened(expected, Times.Exactly);
    }

    [Fact]
    public async Task Announcement_IsPublishedOnlyAfterTheEndpointIsReady()
    {
        var rig = new Rig();

        var starting = rig.Sut.InitializeAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(starting.IsCompleted);
        rig.AssertAnnouncements(0);
        Assert.False(rig.Host.Instance.Started.Task.IsCompleted);

        rig.Ready.SetResult(default!);
        await starting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        rig.AssertAnnouncements(1);
        Assert.True(rig.Host.Instance.Started.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Endpoints_AreSharedAndSuppressFaults()
    {
        var rig = new Rig();
        rig.Ready.SetResult(default!);

        await rig.Sut.InitializeAsync();

        A.CallTo(() => rig.Control.RegisterCommandConsumer(CkMethodQueueNames.For(TestData.MethodId),
                A<ExecuteDeliveredCommandHandler<CkMethodRequest>>._,
                A<CommandConsumerOptions>.That.Matches(o => o.SuppressFaults && !o.ConfigureConsumeTopology)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EndpointThatNeverGetsReady_FailsTheStart_AndIsNeverAnnounced()
    {
        var rig = new Rig(TimeSpan.FromMilliseconds(150));

        var act = () => rig.Sut.InitializeAsync();

        var ex = await Assert.ThrowsAsync<TimeoutException>(act);
        Assert.Contains(TestData.MethodId, ex.Message.Replace("-", "-"), StringComparison.OrdinalIgnoreCase);
        rig.AssertAnnouncements(0);
        Assert.True(rig.Host.Instance.Started.Task.IsFaulted);
    }
}
