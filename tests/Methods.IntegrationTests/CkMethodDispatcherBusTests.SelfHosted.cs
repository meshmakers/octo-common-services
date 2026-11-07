using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     AB#8411 against the real broker: a service that hosts a handler and calls it itself (a REST facade) through its own
///     dispatcher. A bus-registered handler answers the local call with the envelope a remote caller gets from the same host
///     (byte-identical, same handler instance id); a local-only handler has no queue, so another process gets
///     <c>METHOD_HANDLER_NOT_AVAILABLE</c> while the hosting process is served.
/// </summary>
public sealed partial class CkMethodDispatcherBusTests
{
    /// <summary>One service: handler host (bus or local only) and dispatcher with registry feed.</summary>
    private async Task<(Host Host, ICkMethodDispatcher Dispatcher, RecordingFilter Filter, HandlerProbe Probe)>
        StartSelfHostAsync(bool localOnly, string serviceName = "SelfHost")
    {
        var probe = new HandlerProbe();
        var filter = new RecordingFilter();
        var services = BaseServices();
        services.AddSingleton(probe);
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddSingleton<ICkMethodInvocationFilter>(filter);
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = serviceName;
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddSingleton<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
        services.AddCkMethodHandlers(serviceName, m =>
        {
            if (localOnly)
            {
                m.HandleLocalOnly<ChangePasswordHandler>(MethodId);
            }
            else
            {
                m.Handle<ChangePasswordHandler>(MethodId);
            }
        });
        services.AddCkMethodDispatcher();

        var host = await StartAsync(services);
        return (host, host.Provider.GetRequiredService<ICkMethodDispatcher>(), filter, probe);
    }

    public static TheoryData<string> SelfCases => ["success", "declared-error", "thrown"];

    [Theory]
    [MemberData(nameof(SelfCases))]
    public async Task SelfHost_BusHandler_LocalCall_IsByteIdenticalToTheRemoteCallOfTheSameHost(string name)
    {
        var (_, self, selfFilter, probe) = await StartSelfHostAsync(localOnly: false);
        var (_, remote, remoteFilter) = await StartGatewayAsync(name: "RemoteGateway");
        var request = Case(name);

        var overBus = await remote.InvokeAsync(request, TestContext.Current.CancellationToken);
        var local = await self.InvokeAsync(request, TestContext.Current.CancellationToken);

        // not normalized: the same host answers both, so even the handler instance id is the same
        Assert.Equal(JsonSerializer.Serialize(overBus, Wire), JsonSerializer.Serialize(local, Wire));
        Assert.NotEmpty(local.HandlerInstanceId);
        Assert.Equal(2, probe.Calls); // once over the bus, once in-process, both in the self host
        Assert.Equal([CkMethodTransportNames.Bus], remoteFilter.Transports);
        Assert.Equal([CkMethodTransportNames.InProcess], selfFilter.Transports);
        Assert.DoesNotContain("never-on-the-wire", JsonSerializer.Serialize(local, Wire));
    }

    [Fact]
    public async Task SelfHost_LocalOnly_HasNoQueue_AnotherProcessGetsNotAvailable_TheHostIsServed()
    {
        var (_, self, selfFilter, probe) = await StartSelfHostAsync(localOnly: true);
        var (_, remote, _) = await StartGatewayAsync(name: "RemoteGateway");

        var fromElsewhere = await remote.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        var fromHost = await self.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, fromElsewhere.Error!.Code);
        Assert.Equal(MethodId, fromElsewhere.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.True(fromHost.Success, JsonSerializer.Serialize(fromHost, Wire));
        Assert.Equal("21", fromHost.ResultJson);
        Assert.Equal(1, probe.Calls);
        Assert.Equal([CkMethodTransportNames.InProcess], selfFilter.Transports);
    }
}
