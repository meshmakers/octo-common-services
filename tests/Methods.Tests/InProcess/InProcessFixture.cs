using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>Builds the in-process transport around one handler type with a fake clock and a log collector.</summary>
internal sealed class InProcessFixture
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    public FakeLogCollector Logs { get; } = new();

    public CkInProcessMethodRegistrations Registrations { get; } = new();

    public ICkMethodInvocationPipeline Pipeline { get; private set; } = null!;

    public ICkInProcessMethodTransport Transport { get; private set; } = null!;

    public IServiceProvider Provider { get; private set; } = null!;

    /// <summary>The factory of the invoker and of the handlers; one factory = one logging scope provider.</summary>
    public ILoggerFactory Loggers { get; private set; } = null!;

    public static InProcessFixture With<THandler>(THandler handler, CkMethodDto? definition = null,
        string methodId = TestData.MethodId, params ICkMethodInvocationFilter[] filters)
        where THandler : class, ICkMethodHandler
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => handler);
        return Build<THandler>(services, definition, methodId, filters);
    }

    public static InProcessFixture Build<THandler>(IServiceCollection services, CkMethodDto? definition = null,
        string methodId = TestData.MethodId, params ICkMethodInvocationFilter[] filters)
        where THandler : class, ICkMethodHandler =>
        BuildWithProbes<THandler>(services, [], definition, methodId, filters);

    public static InProcessFixture BuildWithProbes<THandler>(IServiceCollection services,
        IReadOnlyCollection<ICkInProcessAmbientStateProbe> probes, CkMethodDto? definition = null,
        string methodId = TestData.MethodId, params ICkMethodInvocationFilter[] filters)
        where THandler : class, ICkMethodHandler
    {
        var fixture = new InProcessFixture();
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(fixture.Logs)));
        fixture.Loggers = loggers;
        services.AddSingleton(loggers);
        var provider = services.BuildServiceProvider();
        fixture.Provider = provider;
        var registration = fixture.Registrations.Add(methodId, typeof(THandler));
        if (definition != null)
        {
            new CkMethodRegistrationValidator(new FixedDefinitionSource(definition)).Validate(fixture.Registrations);
            Assert.NotNull(registration.Definition);
        }
        else
        {
            // a bare definition (no authorization): default-deny admits an administrator (AB#7664)
            registration.Definition = new CkMethodDto { MethodId = methodId };
        }

        fixture.Pipeline = new CkMethodInvocationPipeline(filters, new DefinitionSensitivity(definition), loggers.CreateLogger<CkMethodInvocationPipeline>());
        fixture.Transport = new CkInProcessMethodTransport(provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Registrations, fixture.Pipeline, Options.Create(new CkMethodHandlerOptions()), fixture.Time,
            loggers, probes,
            provider.GetService<ICkMethodPrincipalProvider>() ?? new AdministratorProvider(),
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())));
        return fixture;
    }

    public DateTime InSeconds(int seconds) => Time.GetUtcNow().UtcDateTime.AddSeconds(seconds);

    public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));
}

/// <summary>The default caller of the in-process tests: the ambient principal, else an administrator.</summary>
internal sealed class AdministratorProvider : ICkMethodPrincipalProvider
{
    public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) =>
        CkMethodPrincipalScope.CurrentPrincipal ?? HostFixture.Administrator;
}

/// <summary>A caller that never established a principal.</summary>
internal sealed class NoPrincipalProvider : ICkMethodPrincipalProvider
{
    public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) => null;
}

/// <summary>Sensitive parameters read from a fixed definition (<c>null</c> definition: method unknown, fail-closed).</summary>
internal sealed class DefinitionSensitivity(CkMethodDto? definition) : ICkMethodSensitivityResolver
{
    public IReadOnlySet<string>? GetSensitiveParameters(string tenantId, string ckTypeId, string qualifiedMethodId) =>
        definition == null
            ? null
            : (definition.Parameters ?? []).Where(p => p.Sensitive).Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsResultSensitive(string tenantId, string ckTypeId, string qualifiedMethodId) =>
        definition == null || definition.Result?.Sensitive == true;
}
