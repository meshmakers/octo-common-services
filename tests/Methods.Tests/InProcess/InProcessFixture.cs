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
        where THandler : class, ICkMethodHandler
    {
        var fixture = new InProcessFixture();
        var provider = services.BuildServiceProvider();
        var registration = fixture.Registrations.Add(methodId, typeof(THandler));
        if (definition != null)
        {
            new CkMethodRegistrationValidator(new FixedDefinitionSource(definition)).Validate(fixture.Registrations);
            Assert.NotNull(registration.Definition);
        }

        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(fixture.Logs)));
        fixture.Pipeline = new CkMethodInvocationPipeline(filters, loggers.CreateLogger<CkMethodInvocationPipeline>());
        fixture.Transport = new CkInProcessMethodTransport(provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Registrations, fixture.Pipeline, Options.Create(new CkMethodHandlerOptions()), fixture.Time,
            loggers);
        return fixture;
    }

    public DateTime InSeconds(int seconds) => Time.GetUtcNow().UtcDateTime.AddSeconds(seconds);

    public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));
}
