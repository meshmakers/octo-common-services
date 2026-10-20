using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

/// <summary>Builds the invoker around one handler type with a fake clock and a log collector.</summary>
internal sealed class HostFixture
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public FakeLogCollector Logs { get; } = new();

    public CkMethodHandlerRegistrations Registrations { get; } = new();

    public CkMethodHandlerInstance Instance { get; private set; } = null!;

    public ICkMethodHandlerInvoker Invoker { get; private set; } = null!;

    public CkMethodBusConsumer BusConsumer { get; private set; } = null!;

    public static HostFixture With<THandler>(THandler handler, CkMethodDto? definition = null,
        string methodId = TestData.MethodId, ICkMethodCallerTokenValidator? tokenValidator = null)
        where THandler : class, ICkMethodHandler
    {
        var fixture = new HostFixture();
        var services = new ServiceCollection();
        services.AddScoped(_ => handler);
        var provider = services.BuildServiceProvider();

        fixture.Registrations.SetServiceName("TestService");
        var registration = fixture.Registrations.Add(methodId, typeof(THandler));
        if (definition != null)
        {
            new CkMethodRegistrationValidator(new FixedDefinitionSource(definition)).Validate(fixture.Registrations);
            Assert.NotNull(registration.Definition);
        }

        fixture.Instance = new CkMethodHandlerInstance(fixture.Registrations,
            Options.Create(new CkMethodHandlerOptions()), fixture.Time);
        var invoker = new CkMethodHandlerInvoker(provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Registrations, fixture.Instance, fixture.Time,
            new FakeLogger<CkMethodHandlerInvoker>(fixture.Logs), tokenValidator: tokenValidator);
        fixture.Invoker = invoker;
        fixture.BusConsumer = new CkMethodBusConsumer(invoker, fixture.Instance, fixture.Time,
            new FakeLogger<CkMethodBusConsumer>(fixture.Logs));
        return fixture;
    }

    public DateTime InSeconds(int seconds) => Time.GetUtcNow().UtcDateTime.AddSeconds(seconds);

    public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));
}

internal sealed class FixedDefinitionSource(CkMethodDto? definition, params string[] tenants) : ICkMethodDefinitionSource
{
    public IReadOnlyList<string> TenantIds { get; } = tenants.Length > 0 ? tenants : ["octosystem"];

    public CkMethodDto? Find(string qualifiedMethodId) => definition;
}

internal sealed class ChangePasswordHandler(
    Func<CkMethodContext<UserChangePasswordParameters>, CancellationToken, Task<CkMethodOutcome<UserChangePasswordResult>>>? run = null)
    : ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>
{
    public int Calls { get; private set; }

    public CkMethodContext<UserChangePasswordParameters>? LastContext { get; private set; }

    public Task<CkMethodOutcome<UserChangePasswordResult>> HandleAsync(
        CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken)
    {
        Calls++;
        LastContext = context;
        return run?.Invoke(context, cancellationToken) ??
               Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
                   new UserChangePasswordResult { Value = "changed" });
    }
}

internal sealed class NoResultHandler(
    Func<CkMethodContext<UserChangePasswordParameters>, Task<CkMethodOutcome>>? run = null)
    : ICkMethodHandler<UserChangePasswordParameters>
{
    public Task<CkMethodOutcome> HandleAsync(CkMethodContext<UserChangePasswordParameters> context,
        CancellationToken cancellationToken) => run?.Invoke(context) ?? Task.FromResult(CkMethodOutcome.Ok());
}

/// <summary>Transport delivery info of the test deliveries.</summary>
internal static class TestDelivery
{
    public static readonly Meshmakers.Octo.Common.DistributionEventHub.Services.CommandDeliveryInfo First =
        new(false, 0, 0, Guid.NewGuid());

    public static readonly Meshmakers.Octo.Common.DistributionEventHub.Services.CommandDeliveryInfo Redelivered =
        new(true, 0, 0, Guid.NewGuid());

    public static readonly Meshmakers.Octo.Common.DistributionEventHub.Services.CommandDeliveryInfo Retried =
        new(false, 1, 0, Guid.NewGuid());
}
