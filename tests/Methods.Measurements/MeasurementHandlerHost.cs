using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Services.Methods.Measurements;

// This file is compiled into the measurement tests AND the worker process (Methods.Measurements.Worker links it), so the
// replica that is hard-killed runs exactly the handler host the in-process replicas run.

/// <summary>What a replica does for every invocation it executes (the handler body, not the delivery).</summary>
public sealed class MeasurementProbe(string instanceLabel, TimeSpan work, Action<Guid, string> onExecute)
{
    public string InstanceLabel { get; } = instanceLabel;
    public TimeSpan Work { get; } = work;
    public void Executed(Guid invocationId) => onExecute(invocationId, InstanceLabel);
}

public sealed record MeasurementParameters
{
    public required string NewPassword { get; init; }
}

public sealed record MeasurementResult
{
    public int? Value { get; init; }
}

/// <summary>Records the execution first (so a hard kill during the work still counts as an execution), then works.</summary>
public sealed class MeasurementHandler(MeasurementProbe probe) : ICkMethodHandler<MeasurementParameters, MeasurementResult>
{
    private static readonly bool Jitter = Environment.GetEnvironmentVariable("OCTO_METHOD_MEASURE_JITTER") == "1";

    public async Task<CkMethodOutcome<MeasurementResult>> HandleAsync(CkMethodContext<MeasurementParameters> context,
        CancellationToken cancellationToken)
    {
        probe.Executed(context.InvocationId);
        if (probe.Work > TimeSpan.Zero)
        {
            // OCTO_METHOD_MEASURE_JITTER=1: work time 0.5 to 1.5 times the nominal one, so the calls of the caller threads
            // do not complete in lock step (steady arrivals instead of synchronized batches).
            var work = Jitter ? probe.Work * (0.5 + Random.Shared.NextDouble()) : probe.Work;
            await Task.Delay(work, cancellationToken);
        }

        return new MeasurementResult { Value = context.Parameters.NewPassword.Length };
    }
}

internal sealed class MeasurementDefinitionSource : ICkMethodDefinitionSource
{
    public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

    public CkMethodDto? Find(string qualifiedMethodId) => qualifiedMethodId != MeasurementHost.MethodId
        ? null
        : new CkMethodDto
        {
            MethodId = "ChangePassword-1",
            Parameters = [new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true }],
            Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }
        };
}

/// <summary>The measurement exercises transport and delivery, not authorization (covered by the adversarial suite).</summary>
internal sealed class MeasurementAllowAnyCallerEvaluator : ICkMethodAuthorizationEvaluator
{
    public CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
        string? targetRtId, CkMethodPrincipal? principal) => CkMethodAuthorizationDecision.Allow;
}

/// <summary>One service host (own service provider, own bus connection, own handler instance id).</summary>
public sealed class MeasurementHost(ServiceProvider provider) : IAsyncDisposable
{
    public const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    public const string HandlerServiceName = "MeasuredService";

    private readonly List<IHostedService> _started = [];
    private bool _stopped;

    public ServiceProvider Provider { get; } = provider;

    public static ServiceCollection BaseServices(string brokerHost, int brokerPort, string instancePrefix)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = instancePrefix;
            o.BrokerHost = brokerHost;
            o.BrokerPort = (ushort)brokerPort;
            o.BrokerUser = "guest";
            o.BrokerPassword = "guest";
        });
        return services;
    }

    /// <summary>Starts a handler host that shares the method queue with its sibling replicas.</summary>
    public static async Task<MeasurementHost> StartHandlerAsync(string brokerHost, int brokerPort, string instancePrefix,
        MeasurementProbe probe)
    {
        var services = BaseServices(brokerHost, brokerPort, instancePrefix);
        services.AddSingleton(probe);
        services.AddSingleton<ICkMethodDefinitionSource>(new MeasurementDefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = HandlerServiceName;
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddSingleton<ICkMethodAuthorizationEvaluator, MeasurementAllowAnyCallerEvaluator>();
        services.AddCkMethodHandlers(HandlerServiceName, m => m.Handle<MeasurementHandler>(MethodId));
        return await StartAsync(services);
    }

    public static async Task<MeasurementHost> StartAsync(ServiceCollection services)
    {
        var host = new MeasurementHost(services.BuildServiceProvider());
        await host.StartAsync();
        return host;
    }

    private async Task StartAsync()
    {
        await Provider.GetRequiredService<IEventHubControl>().StartAsync();
        using (var scope = Provider.CreateScope())
        {
            foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
            {
                await service.InitializeAsync();
            }
        }

        foreach (var hosted in Provider.GetServices<IHostedService>().Where(h => h.GetType().Name == "CkMethodHandlerHeartbeatService"))
        {
            await hosted.StartAsync(CancellationToken.None);
            _started.Add(hosted);
        }
    }

    /// <summary>Graceful stop: heartbeat down, withdraw, endpoints removed, bus stopped, provider disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        try
        {
            foreach (var hosted in _started)
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            await Provider.GetRequiredService<IEventHubControl>().StopAsync();
        }
        catch
        {
            // best effort
        }

        await Provider.DisposeAsync();
    }
}
