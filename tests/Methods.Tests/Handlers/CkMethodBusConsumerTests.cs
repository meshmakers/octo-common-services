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

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)), r =>
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

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)),
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
}
