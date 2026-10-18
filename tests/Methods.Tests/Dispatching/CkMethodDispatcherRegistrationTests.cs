using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Dispatching;

public class CkMethodDispatcherRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(A.Fake<IDistributionEventHubService>());
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return services;
    }

    [Fact]
    public void AddCkMethodDispatcher_ResolvesTheDispatcherRegistryAndPipeline_WithoutCkCache()
    {
        var services = Services();

        services.AddCkMethodDispatcher();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<CkMethodDispatcher>(provider.GetRequiredService<ICkMethodDispatcher>());
        Assert.Same(provider.GetRequiredService<ICkMethodHandlerRegistry>(),
            provider.GetRequiredService<CkMethodHandlerRegistry>());
        Assert.NotNull(provider.GetRequiredService<ICkMethodInvocationPipeline>());
    }

    [Fact]
    public void AddCkMethodDispatcher_IsIdempotent_AndAddsOneDiscoveryStartupService()
    {
        var services = Services();

        services.AddCkMethodDispatcher().AddCkMethodDispatcher();

        Assert.Single(services, d => d.ImplementationType == typeof(CkMethodDispatcherStartupService));
        Assert.Single(services, d => d.ServiceType == typeof(ICkMethodDispatcher));
        Assert.Single(services, d => d.ServiceType == typeof(ICkMethodInvocationPipeline));
    }

    [Fact]
    public void DispatcherAndInProcessTransport_ShareOnePipeline_SoAFilterIsRegisteredOnce()
    {
        var services = Services();
        services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        services.AddCkMethodDispatcher();
        services.AddCkMethodInvocationFilter<NoopFilter>();
        using var provider = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(ICkMethodInvocationPipeline));
        Assert.Single(provider.GetServices<ICkMethodInvocationFilter>());
        Assert.NotNull(provider.GetRequiredService<ICkInProcessMethodTransport>());
        Assert.NotNull(provider.GetRequiredService<ICkMethodDispatcher>());
    }

    [Fact]
    public void DiscoveryRequest_RunsAfterTheEventHubAndTheHandlerEndpoints()
    {
        var service = new CkMethodDispatcherStartupService(A.Fake<IDistributionEventHubService>(), TimeProvider.System,
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodDispatcherStartupService>());

        Assert.True(service.Order > 25, "after EventHubStartupService (20) and the endpoints of handlers in the same process (25)");
    }

    [Fact]
    public async Task DiscoveryRequest_FailingPublish_IsNotFatal()
    {
        var hub = A.Fake<IDistributionEventHubService>();
        A.CallTo(hub).Where(c => c.Method.Name == "PublishAsync").Throws<InvalidOperationException>();
        var service = new CkMethodDispatcherStartupService(hub, TimeProvider.System,
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodDispatcherStartupService>());

        await service.InitializeAsync();
    }

    private sealed class NoopFilter : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Audit;

        public Task<Contracts.DistributionEventHub.Methods.CkMethodResponse> InvokeAsync(
            CkMethodInvocationContext context, CkMethodInvocationDelegate next) => next(context.Request);
    }
}
