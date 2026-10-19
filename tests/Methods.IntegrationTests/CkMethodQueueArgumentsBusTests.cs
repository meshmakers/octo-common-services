using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     A method queue that already exists with different arguments (AB#6908, rolling update between two builds): the new
///     replica must not run, announce or take the queue over; it fails its start with a diagnosable error, the old
///     queue stays as it is, and a start succeeds again as soon as the old queue is gone.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodQueueArgumentsBusTests(RabbitMqFixture rabbitMq, ITestOutputHelper output) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly string _prefix = $"qargs-{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _providers = [];
    private IConnection? _connection;
    private IChannel? _channel;

    public async ValueTask InitializeAsync()
    {
        var factory = new ConnectionFactory { HostName = rabbitMq.Host, Port = rabbitMq.Port, UserName = "guest", Password = "guest" };
        _connection = await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            try
            {
                await provider.GetRequiredService<IEventHubControl>().StopAsync();
            }
            catch
            {
                // best effort
            }

            await provider.DisposeAsync();
        }

        if (_channel != null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }

    public sealed record Parameters
    {
        public required string NewPassword { get; init; }
    }

    public sealed record Result
    {
        public int? Value { get; init; }
    }

    public sealed class Handler : ICkMethodHandler<Parameters, Result>
    {
        public Task<CkMethodOutcome<Result>> HandleAsync(CkMethodContext<Parameters> context, CancellationToken ct) =>
            Task.FromResult<CkMethodOutcome<Result>>(new Result { Value = context.Parameters.NewPassword.Length });
    }

    private sealed class DefinitionSource : ICkMethodDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) => qualifiedMethodId != MethodId
            ? null
            : new CkMethodDto
            {
                MethodId = "ChangePassword-1",
                Parameters = [new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true }],
                Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }
            };
    }

    private string QueueName => $"{_prefix}-{CkMethodQueueNames.For(MethodId)}";

    private ServiceProvider BuildProvider(bool handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = _prefix;
            o.BrokerHost = rabbitMq.Host;
            o.BrokerPort = (ushort)rabbitMq.Port;
            o.BrokerUser = "guest";
            o.BrokerPassword = "guest";
        });
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        if (handler)
        {
            services.Configure<CkMethodHandlerOptions>(o => o.EndpointReadyTimeout = ReadyTimeout);
            services.AddDistributionEventHub(c =>
            {
                c.UniqueServiceAddress = "S";
                c.AutomaticallyStartBusDuringStartup = false;
                c.AddCkMethodHandlerDiscovery();
            });
            services.AddCkMethodHandlers("S", m => m.Handle<Handler>(MethodId));
        }
        else
        {
            services.AddDistributionEventHub(c =>
            {
                c.UniqueServiceAddress = "Gateway";
                c.AutomaticallyStartBusDuringStartup = false;
                c.AddCkMethodHandlerRegistryFeed();
            });
            services.AddCkMethodDispatcher();
        }

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static async Task StartAsync(ServiceProvider provider)
    {
        await provider.GetRequiredService<IEventHubControl>().StartAsync();
        using var scope = provider.CreateScope();
        foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
        {
            await service.InitializeAsync();
        }
    }

    private static CkMethodRequest NewRequest() => new("meshtest")
    {
        InvocationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = "acc-1",
        ParametersJson = JsonSerializer.Serialize(new { newPassword = "correct-horse-battery" }),
        DeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(10)
    };

    [Fact]
    public async Task ExistingQueueWithDifferentArguments_FailsTheStartWithADiagnosableError_AndIsNotTakenOver()
    {
        await _channel!.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: true,
            arguments: new Dictionary<string, object?> { ["x-message-ttl"] = 60000 },
            cancellationToken: TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => StartAsync(BuildProvider(handler: true)));
        output.WriteLine($"start failed after {sw.Elapsed.TotalSeconds:F1} s: {error.Message}");

        // Fails at the configured readiness timeout (not hanging, not a silent half-started host) and says what to look for.
        Assert.InRange(sw.Elapsed, ReadyTimeout - TimeSpan.FromMilliseconds(500), ReadyTimeout + TimeSpan.FromSeconds(15));
        Assert.Contains(CkMethodQueueNames.For(MethodId), error.Message);
        Assert.Contains("different arguments", error.Message);

        // The queue of the other build is untouched, nothing was announced, and a caller is told so at once.
        var queues = await rabbitMq.RabbitMqCtlAsync("list_queues", "name", "arguments", "consumers");
        var line = Assert.Single(queues, l => l.StartsWith(QueueName + "\t", StringComparison.Ordinal));
        Assert.Contains("x-message-ttl", line);
        Assert.EndsWith("\t0", line);

        var gateway = BuildProvider(handler: false);
        await StartAsync(gateway);
        var response = await gateway.GetRequiredService<ICkMethodDispatcher>().InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
    }

    [Fact]
    public async Task OnceTheOldQueueIsGone_TheNewReplicaStartsAndServes()
    {
        await _channel!.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: true,
            arguments: new Dictionary<string, object?> { ["x-message-ttl"] = 60000 },
            cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => StartAsync(BuildProvider(handler: true)));

        // The last replica of the old build is gone: its auto-delete queue disappears (simulated by deleting it).
        await _channel.QueueDeleteAsync(QueueName, cancellationToken: TestContext.Current.CancellationToken);

        var restarted = BuildProvider(handler: true);
        await StartAsync(restarted);
        foreach (var hosted in restarted.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        var gateway = BuildProvider(handler: false);
        await StartAsync(gateway);
        var response = await gateway.GetRequiredService<ICkMethodDispatcher>().InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.True(response.Success, response.Error?.Code);
        var queues = await rabbitMq.RabbitMqCtlAsync("list_queues", "name", "arguments");
        Assert.DoesNotContain(queues, l => l.StartsWith(QueueName + "\t", StringComparison.Ordinal) && l.Contains("x-message-ttl"));
    }
}
