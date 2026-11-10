using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Operations;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>
///     The invoker of a handler host with the operation runtime (<c>AddCkMethodOperations</c>) and a store (in-memory
///     unless one is given), a fake clock and a log collector (AB#8443).
/// </summary>
internal sealed class AsyncOperationFixture
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    public FakeLogCollector Logs { get; } = new();

    public ICkMethodOperationStore Store { get; private set; } = null!;

    public CkMethodOperationRunner Runner { get; private set; } = null!;

    public ICkMethodHandlerInvoker Invoker { get; private set; } = null!;

    public CkMethodHandlerInstance Instance { get; private set; } = null!;

    public IServiceProvider Provider { get; private set; } = null!;

    /// <summary>The async definition of <see cref="TestData.Definition" /> (string result, sensitive parameters).</summary>
    public static CkMethodDto AsyncDefinition(bool sensitiveResult = false, params string[] errors)
    {
        var definition = TestData.Definition(true, errors);
        definition.Execution = new CkMethodExecutionDto { Mode = CkMethodModes.Async };
        definition.Result!.Sensitive = sensitiveResult;
        return definition;
    }

    public static AsyncOperationFixture With<THandler>(THandler handler, CkMethodDto? definition = null,
        ICkMethodOperationStore? store = null, bool withOperations = true,
        CkMethodPrincipal? principal = null, string methodId = TestData.MethodId)
        where THandler : class, ICkMethodHandler
    {
        var fixture = new AsyncOperationFixture { MethodId = methodId };
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(fixture.Time);
        services.AddLogging(b => b.AddProvider(new FakeLoggerProvider(fixture.Logs)));
        services.AddScoped(_ => handler);
        if (store != null)
        {
            services.AddSingleton(store);
        }

        services.AddCkMethodInMemoryOperationStore();
        if (withOperations)
        {
            services.AddCkMethodOperations();
        }
        else
        {
            services.AddCkMethodOperationSweep();
        }

        var provider = services.BuildServiceProvider();
        fixture.Provider = provider;
        fixture.Store = provider.GetRequiredService<ICkMethodOperationStore>();
        fixture.Runner = provider.GetRequiredService<CkMethodOperationRunner>();

        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        var registration = registrations.Add(methodId, typeof(THandler));
        new CkMethodRegistrationValidator(new FixedDefinitionSource(definition ?? AsyncDefinition()))
            .Validate(registrations);
        Assert.NotNull(registration.Definition);

        fixture.Instance = new CkMethodHandlerInstance(registrations, Options.Create(new CkMethodHandlerOptions()),
            fixture.Time);
        fixture.Invoker = new CkMethodHandlerInvoker(provider.GetRequiredService<IServiceScopeFactory>(),
            registrations, fixture.Instance, fixture.Time,
            provider.GetRequiredService<ILogger<CkMethodHandlerInvoker>>(),
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            tokenValidator: new FixedPrincipalValidator(principal ?? HostFixture.Administrator));
        return fixture;
    }

    public string MethodId { get; private init; } = TestData.MethodId;

    public CkMethodRequest Request() =>
        TestData.Request(Time.GetUtcNow().UtcDateTime.AddSeconds(30)) with { MethodId = MethodId };

    public Task<CkMethodResponse> InvokeAsync(CkMethodRequest? request = null) =>
        Invoker.InvokeAsync(request ?? Request(), TestContext.Current.CancellationToken);

    public async Task<CkMethodOperationRecord> FinishedAsync(CkMethodResponse accepted)
    {
        Assert.True(accepted.IsAccepted, accepted.ToString());
        var operationId = accepted.Operation!.OperationId;
        await Runner.WhenFinishedAsync("meshtest", operationId).WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        return (await Store.GetAsync("meshtest", operationId, TestContext.Current.CancellationToken))!;
    }

    public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));
}

/// <summary>An async handler whose accept step and work are given by the test.</summary>
internal sealed class ExportHandler(
    Func<ICkMethodOperationProgress, CancellationToken, Task<CkMethodOutcome<UserChangePasswordResult>>> work,
    Func<CkMethodContext<UserChangePasswordParameters>, CkMethodOutcome<UserChangePasswordResult>?>? accept = null)
    : ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>
{
    public int Accepts;

    public Task<CkMethodOutcome<UserChangePasswordResult>> HandleAsync(
        CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Accepts);
        return Task.FromResult(accept?.Invoke(context) ?? CkMethodOutcome<UserChangePasswordResult>.Accepted(work));
    }
}
