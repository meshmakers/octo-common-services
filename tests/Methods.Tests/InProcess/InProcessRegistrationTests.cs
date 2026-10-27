using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

public class InProcessRegistrationTests
{
    private const string OtherMethodId = "System.Identity/User.ResetPassword-1";

    [Fact]
    public async Task InProcessHandlers_NeverTouchTheBus_AndAreNotAnnounced()
    {
        var hub = A.Fake<IDistributionEventHubService>();
        var control = A.Fake<IEventHubControl>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(hub);
        services.AddSingleton(control);
        services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        await using var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<ICkInProcessMethodTransport>();
        provider.GetRequiredService<CkInProcessMethodRegistrations>().Handlers[TestData.MethodId].Definition =
            TestData.Definition();

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(HostFixture.Administrator))
        {
            response = await transport.InvokeAsync(
                TestData.Request(DateTime.UtcNow.AddSeconds(15)), TestContext.Current.CancellationToken);
        }

        Assert.True(response.Success);
        Assert.Empty(Fake.GetCalls(hub));
        Assert.Empty(Fake.GetCalls(control));
        Assert.Equal([TestData.MethodId], transport.MethodIds);
        // no remote host: no handler instance, no heartbeat, no bus consumer
        Assert.Null(provider.GetService<CkMethodHandlerInstance>());
        Assert.Null(provider.GetService<CkMethodBusConsumer>());
        Assert.Null(provider.GetService<ICkMethodHandlerInvoker>());
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(CkMethodHandlerStartupService));
    }

    [Fact]
    public async Task SameHandlerClass_WorksUnderBothTransports_WithoutCodeChanges()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(A.Fake<IDistributionEventHubService>());
        services.AddSingleton(A.Fake<IEventHubControl>());
        services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<CkInProcessMethodRegistrations>().Handlers[TestData.MethodId].Definition =
            TestData.Definition();
        CkMethodResponse inProcess;
        using (CkMethodPrincipalScope.Begin(HostFixture.Administrator))
        {
            inProcess = await provider.GetRequiredService<ICkInProcessMethodTransport>().InvokeAsync(
                TestData.Request(DateTime.UtcNow.AddSeconds(15)), TestContext.Current.CancellationToken);
        }
        var remoteHost = HostFixture.With(new ChangePasswordHandler(), TestData.Definition(), OtherMethodId);
        var remoteResponse = await remoteHost.Invoker.InvokeAsync(
            TestData.Request(remoteHost.InSeconds(15)) with { MethodId = OtherMethodId },
            TestContext.Current.CancellationToken);

        Assert.Equal(remoteResponse.ResultJson, inProcess.ResultJson);
        Assert.True(inProcess.Success && remoteResponse.Success);
    }

    [Fact]
    public async Task MethodRegisteredInProcessAndRemote_StopsTheStartup()
    {
        var remote = new CkMethodHandlerRegistrations();
        remote.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var inProcess = new CkInProcessMethodRegistrations();
        inProcess.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var provider = new ServiceCollection().AddSingleton(remote).BuildServiceProvider();
        var validation = new CkInProcessMethodValidationService(inProcess, provider,
            new CkMethodRegistrationValidator(new FixedDefinitionSource(TestData.Definition())),
            Options.Create(new CkMethodHandlerOptions()), NullLogger<CkInProcessMethodValidationService>.Instance);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(validation.InitializeAsync);

        Assert.Contains(TestData.MethodId, e.Message);
    }

    [Fact]
    public async Task Validation_RecordsTheDefinition_AndFailsOnAnUnknownMethod()
    {
        var inProcess = new CkInProcessMethodRegistrations();
        var registration = inProcess.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var provider = new ServiceCollection().BuildServiceProvider();
        var ok = new CkInProcessMethodValidationService(inProcess, provider,
            new CkMethodRegistrationValidator(new FixedDefinitionSource(TestData.Definition())),
            Options.Create(new CkMethodHandlerOptions()), NullLogger<CkInProcessMethodValidationService>.Instance);
        var unknown = new CkInProcessMethodValidationService(inProcess, provider,
            new CkMethodRegistrationValidator(new FixedDefinitionSource(null)),
            Options.Create(new CkMethodHandlerOptions()), NullLogger<CkInProcessMethodValidationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(unknown.InitializeAsync);
        await ok.InitializeAsync();

        Assert.NotNull(registration.Definition);
    }

    [Fact]
    public void AddingTheSameHandlerTwiceForOneMethod_IsRejected()
    {
        var services = new ServiceCollection();
        services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId)));
    }
}
