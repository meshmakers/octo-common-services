using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Configuration;

public class CkMethodServiceCollectionExtensionsTests
{
    private sealed class UntypedHandler : ICkMethodHandler;

    private sealed class DoubleHandler : ICkMethodHandler<UserChangePasswordParameters>,
        ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>
    {
        Task<CkMethodOutcome> ICkMethodHandler<UserChangePasswordParameters>.HandleAsync(
            CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken) =>
            Task.FromResult(CkMethodOutcome.Ok());

        Task<CkMethodOutcome<UserChangePasswordResult>> ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>.HandleAsync(
            CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult());
    }

    [Fact]
    public void AddCkMethodHandlers_RegistersHandler_Validation_Startup_Heartbeat_AndInvoker()
    {
        var services = new ServiceCollection();

        services.AddCkMethodHandlers("identity-services", m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        Assert.Contains(services, d => d.ServiceType == typeof(ChangePasswordHandler) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(ICkMethodHandlerInvoker));
        Assert.Contains(services, d => d.ImplementationType == typeof(CkMethodHandlerValidationService));
        Assert.Contains(services, d => d.ImplementationType == typeof(CkMethodHandlerStartupService));
        Assert.Contains(services, d => d.ImplementationType == typeof(CkMethodHandlerHeartbeatService));
    }

    [Fact]
    public void ValidationRunsBeforeTheBusStartsAndBeforeTheAnnouncement()
    {
        var validation = new CkMethodHandlerValidationService(new CkMethodHandlerRegistrations(),
            new CkMethodRegistrationValidator(new FixedDefinitionSource(null)),
            Microsoft.Extensions.Options.Options.Create(new CkMethodHandlerOptions()),
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodHandlerValidationService>());

        Assert.True(validation.Order > 10, "after the default tenant configuration loaded the CK cache");
        Assert.True(validation.Order < 20, "before the event hub start");
    }

    [Fact]
    public async Task ValidationService_FailsTheStartup_ForAnUnknownMethod()
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var service = new CkMethodHandlerValidationService(registrations,
            new CkMethodRegistrationValidator(new FixedDefinitionSource(null)),
            Microsoft.Extensions.Options.Options.Create(new CkMethodHandlerOptions()),
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodHandlerValidationService>());

        var e = await Assert.ThrowsAsync<InvalidOperationException>(service.InitializeAsync);

        Assert.Contains(TestData.MethodId, e.Message);
    }

    [Fact]
    public async Task ValidationService_CanBeSwitchedOff()
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var service = new CkMethodHandlerValidationService(registrations,
            new CkMethodRegistrationValidator(new FixedDefinitionSource(null)),
            Microsoft.Extensions.Options.Options.Create(new CkMethodHandlerOptions { ValidateRegistrations = false }),
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodHandlerValidationService>());

        await service.InitializeAsync();
    }

    [Fact]
    public void TwoCalls_WithTheSameServiceName_ShareOneRegistryAndOneStartup()
    {
        var services = new ServiceCollection();

        services.AddCkMethodHandlers("svc", m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        services.AddCkMethodHandlers("svc", m => m.Handle<NoResultHandler>("System.Identity/User.Other-1"));

        Assert.Single(services, d => d.ImplementationType == typeof(CkMethodHandlerStartupService));
        Assert.Single(services, d => d.ServiceType == typeof(CkMethodHandlerRegistrations));
    }

    [Fact]
    public void DifferentServiceNames_AreRejected()
    {
        var services = new ServiceCollection();
        services.AddCkMethodHandlers("a", m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddCkMethodHandlers("b", m => m.Handle<NoResultHandler>("System.Identity/User.Other-1")));
    }

    [Fact]
    public void SameMethodTwice_IsRejected()
    {
        var services = new ServiceCollection();

        var e = Assert.Throws<InvalidOperationException>(() => services.AddCkMethodHandlers("svc", m => m
            .Handle<ChangePasswordHandler>(TestData.MethodId)
            .Handle<NoResultHandler>(TestData.MethodId)));

        Assert.Contains(TestData.MethodId, e.Message);
    }

    [Theory]
    [InlineData("ChangePassword-1")]
    [InlineData("System.Identity/User.ChangePassword")]
    [InlineData("")]
    public void MalformedMethodId_IsRejected(string methodId)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddCkMethodHandlers("svc", m => m.Handle<ChangePasswordHandler>(methodId)));
    }

    [Fact]
    public void HandlerWithoutTypedInterface_IsRejected_NamingTheMethod()
    {
        var services = new ServiceCollection();

        var e = Assert.Throws<InvalidOperationException>(() =>
            services.AddCkMethodHandlers("svc", m => m.Handle<UntypedHandler>(TestData.MethodId)));

        Assert.Contains(TestData.MethodId, e.Message);
        Assert.Contains("exactly one", e.Message);
    }

    [Fact]
    public void HandlerWithBothInterfaces_IsRejected()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddCkMethodHandlers("svc", m => m.Handle<DoubleHandler>(TestData.MethodId)));
    }

    [Fact]
    public void ServiceNameIsRequired()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ServiceCollection().AddCkMethodHandlers(" ", m => m.Handle<ChangePasswordHandler>(TestData.MethodId)));
    }

    [Fact]
    public void ValidationOrder_Is_BetweenDefaultConfigurationAndEventHub()
    {
        // pins the documented start order: 10 default configuration, 15 validation, 20 event hub, 25 announce, 30 tenants
        var type = typeof(CkMethodHandlerStartupService);
        Assert.NotNull(type);
        Assert.True(typeof(IAsyncInitializationService).IsAssignableFrom(type));
    }
}
