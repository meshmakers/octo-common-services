using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     Replica sharing and dispatch overhead of the F3.3 dispatcher (AB#6908) against a private RabbitMQ container:
///     several handler hosts of one service (each with its own instance id) consume the one shared method queue, a
///     dispatcher in a gateway host sends the calls.
/// </summary>
/// <remarks>
///     Latency numbers are written to the test output. The default run asserts a conservative bound (build agents are
///     shared); set <c>OCTO_METHOD_PERF_STRICT=1</c> to assert the story's target (p95 below 20 ms) on a quiet machine.
///     See <c>docs/method-performance.md</c>.
/// </remarks>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodReplicaSharingBusTests(RabbitMqFixture rabbitMq, ITestOutputHelper output) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string Tenant = "meshtest";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private readonly string _instancePrefix = $"replicas-{Guid.NewGuid():N}";
    private readonly List<Host> _hosts = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // ---- handler ----------------------------------------------------------------------------------------------

    public sealed record Parameters
    {
        public required string NewPassword { get; init; }
    }

    public sealed record Result
    {
        public int? Value { get; init; }
    }

    /// <summary>What one replica observed. <c>Executions</c> is shared by all replicas of a test.</summary>
    public sealed class Probe(ConcurrentDictionary<Guid, int> executions, TimeSpan work)
    {
        public int Calls;
        public ConcurrentDictionary<Guid, int> Executions { get; } = executions;
        public TimeSpan Work { get; } = work;
    }

    public sealed class Handler(Probe probe) : ICkMethodHandler<Parameters, Result>
    {
        public async Task<CkMethodOutcome<Result>> HandleAsync(CkMethodContext<Parameters> context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Calls);
            probe.Executions.AddOrUpdate(context.InvocationId, 1, (_, n) => n + 1);
            if (probe.Work > TimeSpan.Zero)
            {
                await Task.Delay(probe.Work, cancellationToken);
            }

            return new Result { Value = context.Parameters.NewPassword.Length };
        }
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

    // ---- hosts ------------------------------------------------------------------------------------------------

    private sealed class Host(ServiceProvider provider) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];
        private bool _stopped;

        public ServiceProvider Provider { get; } = provider;

        public async Task StartAsync()
        {
            await Provider.GetRequiredService<IEventHubControl>().StartAsync();
            using (var scope = Provider.CreateScope())
            {
                foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>()
                             .OrderBy(s => s.Order))
                {
                    await service.InitializeAsync();
                }
            }

            foreach (var hosted in Provider.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
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

    private ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = _instancePrefix;
            o.BrokerHost = rabbitMq.Host;
            o.BrokerPort = (ushort)rabbitMq.Port;
            o.BrokerUser = "guest";
            o.BrokerPassword = "guest";
        });
        return services;
    }

    private async Task<Host> StartHandlerHostAsync(Probe probe)
    {
        var services = BaseServices();
        services.AddSingleton(probe);
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "ReplicaService";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddSingleton<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
        services.AddCkMethodHandlers("ReplicaService", m => m.Handle<Handler>(MethodId));
        return await StartAsync(services);
    }

    private async Task<(Host Host, ICkMethodDispatcher Dispatcher)> StartGatewayAsync()
    {
        var services = BaseServices();
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Gateway";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        var host = await StartAsync(services);
        return (host, host.Provider.GetRequiredService<ICkMethodDispatcher>());
    }

    private async Task<Host> StartAsync(ServiceCollection services)
    {
        var host = new Host(services.BuildServiceProvider());
        _hosts.Add(host);
        await host.StartAsync();
        return host;
    }

    private static CkMethodRequest NewRequest() => new(Tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = "acc-1",
        ParametersJson = JsonSerializer.Serialize(new { newPassword = "correct-horse-battery" }),
        DeadlineUtc = DateTime.UtcNow + CallTimeout
    };

    /// <summary>Starts <paramref name="replicas" /> handler hosts of one service; every replica has its own probe.</summary>
    private async Task<List<(Host Host, Probe Probe)>> StartReplicasAsync(int replicas, TimeSpan work,
        ConcurrentDictionary<Guid, int> executions)
    {
        var result = new List<(Host, Probe)>();
        for (var i = 0; i < replicas; i++)
        {
            var probe = new Probe(executions, work);
            result.Add((await StartHandlerHostAsync(probe), probe));
        }

        return result;
    }

    /// <summary>Sends <paramref name="count" /> calls with at most <paramref name="concurrency" /> in flight.</summary>
    private static async Task<List<(CkMethodRequest Request, CkMethodResponse Response)>> RunBurstAsync(
        ICkMethodDispatcher dispatcher, int count, int concurrency, Func<int, Task>? afterCompleted = null)
    {
        var results = new ConcurrentBag<(CkMethodRequest, CkMethodResponse)>();
        var completed = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = TestContext.Current.CancellationToken },
            async (_, ct) =>
            {
                var request = NewRequest();
                var response = await dispatcher.InvokeAsync(request, ct);
                results.Add((request, response));
                var n = Interlocked.Increment(ref completed);
                if (afterCompleted != null)
                {
                    await afterCompleted(n);
                }
            });
        return results.ToList();
    }

    // ---- replica sharing --------------------------------------------------------------------------------------

    public static TheoryData<int, int, double> ReplicaCases => new()
    {
        // replicas, calls, minimum share of each replica. The story: 2 replicas, 1000 calls, at least 30 percent each.
        { 2, 1000, 0.30 },
        { 3, 3000, 0.20 }
    };

    [Theory]
    [MemberData(nameof(ReplicaCases))]
    public async Task Replicas_ShareOneQueue_EachCallIsHandledExactlyOnce_WithASpreadAcrossTheReplicas(int replicas,
        int calls, double minimumShare)
    {
        var executions = new ConcurrentDictionary<Guid, int>();
        var handlers = await StartReplicasAsync(replicas, TimeSpan.FromMilliseconds(2), executions);
        var (_, dispatcher) = await StartGatewayAsync();

        var results = await RunBurstAsync(dispatcher, calls, concurrency: 48);

        Assert.Equal(calls, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.Response.Success, r.Response.Error?.Code);
            Assert.Equal(r.Request.InvocationId, r.Response.InvocationId);
        });
        Assert.Equal(calls, results.Select(r => r.Request.InvocationId).Distinct().Count());

        // Exactly once: every invocation id reached exactly one handler, once.
        Assert.Equal(calls, executions.Count);
        Assert.All(executions, e => Assert.Equal(1, e.Value));
        Assert.Equal(calls, handlers.Sum(h => h.Probe.Calls));

        // The envelope names the executing instance; it must add up to the handlers' own counters.
        var byEnvelope = results.GroupBy(r => r.Response.HandlerInstanceId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(replicas, byEnvelope.Count);
        Assert.Equal(calls, byEnvelope.Values.Sum());

        var shares = handlers.Select(h => h.Probe.Calls).ToList();
        output.WriteLine($"replicas={replicas} calls={calls}: calls per replica = [{string.Join(", ", shares)}]");
        Assert.All(shares, s => Assert.True(s >= calls * minimumShare,
            $"A replica handled {s} of {calls} calls (minimum {minimumShare:P0}); spread [{string.Join(", ", shares)}]"));
    }

    [Fact]
    public async Task ReplicaStoppedMidBurst_NewCallsGoToTheOthers_NoCallIsLostOrExecutedTwice()
    {
        const int calls = 1500;
        var executions = new ConcurrentDictionary<Guid, int>();
        var handlers = await StartReplicasAsync(3, TimeSpan.FromMilliseconds(3), executions);
        var (_, dispatcher) = await StartGatewayAsync();
        var stopped = handlers[0];
        var stopTask = default(Task);
        var callsAtStop = -1;

        var results = await RunBurstAsync(dispatcher, calls, concurrency: 48, afterCompleted: n =>
        {
            if (n == 300 && Interlocked.CompareExchange(ref callsAtStop, stopped.Probe.Calls, -1) == -1)
            {
                // Graceful stop of one replica while the burst is running (not awaited here: the burst keeps going).
                stopTask = Task.Run(async () => await stopped.Host.DisposeAsync());
            }

            return Task.CompletedTask;
        });
        Assert.NotNull(stopTask);
        await stopTask;

        // Every caller got exactly one envelope.
        Assert.Equal(calls, results.Count);
        Assert.Equal(calls, results.Select(r => r.Request.InvocationId).Distinct().Count());
        Assert.All(results, r => Assert.Equal(r.Request.InvocationId, r.Response.InvocationId));

        // Outcomes are a success or one of the two documented in-flight answers; nothing else, nothing lost silently.
        var failures = results.Where(r => !r.Response.Success).ToList();
        var failureCodes = failures.GroupBy(r => r.Response.Error!.Code).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine(
            $"stopped replica handled {stopped.Probe.Calls} calls (at trigger {callsAtStop}); failures: " +
            (failureCodes.Count == 0 ? "none" : string.Join(", ", failureCodes.Select(kv => $"{kv.Key}={kv.Value}"))) +
            $"; per replica [{string.Join(", ", handlers.Select(h => h.Probe.Calls))}]");
        Assert.All(failures, r => Assert.Contains(r.Response.Error!.Code,
            new[] { CkMethodErrorCodes.MethodTimeout, CkMethodErrorCodes.MethodOutcomeUnknown }));

        // Never twice. A call that failed as unknown/timeout may or may not have run, but never more than once.
        Assert.All(executions, e => Assert.Equal(1, e.Value));
        // Every successful call was executed exactly once.
        Assert.All(results.Where(r => r.Response.Success), r => Assert.True(executions.ContainsKey(r.Request.InvocationId)));

        // The remaining replicas took over: the stopped one does not move any more, a follow-up burst succeeds fully.
        var frozen = stopped.Probe.Calls;
        var others = handlers.Skip(1).Select(h => h.Probe.Calls).Sum();
        var after = await RunBurstAsync(dispatcher, 200, concurrency: 16);
        Assert.All(after, r => Assert.True(r.Response.Success, r.Response.Error?.Code));
        Assert.Equal(frozen, stopped.Probe.Calls);
        Assert.Equal(others + 200, handlers.Skip(1).Select(h => h.Probe.Calls).Sum());
    }

    [Fact]
    public async Task LastReplicaStopped_NewCallsFailFastAsHandlerNotAvailable_AndANewReplicaIsServedAgain()
    {
        var executions = new ConcurrentDictionary<Guid, int>();
        var handlers = await StartReplicasAsync(2, TimeSpan.Zero, executions);
        var (_, dispatcher) = await StartGatewayAsync();

        foreach (var h in handlers)
        {
            await h.Host.DisposeAsync();
        }

        var refused = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.False(refused.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, refused.Error!.Code);

        var again = await StartReplicasAsync(1, TimeSpan.Zero, executions);
        Assert.True((await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken)).Success);
        Assert.Equal(1, again[0].Probe.Calls);
    }

    // ---- dispatch overhead ------------------------------------------------------------------------------------

    public static TheoryData<int> LatencyReplicas => [1, 3];

    [Theory]
    [MemberData(nameof(LatencyReplicas))]
    [Trait("Category", "Performance")]
    public async Task DispatchOverhead_WithANoOpHandler_StaysWithinTheBudget(int replicas)
    {
        const int warmUp = 300;
        const int measured = 3000;
        var executions = new ConcurrentDictionary<Guid, int>();
        await StartReplicasAsync(replicas, TimeSpan.Zero, executions);
        var (_, dispatcher) = await StartGatewayAsync();

        for (var i = 0; i < warmUp; i++)
        {
            Assert.True((await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken)).Success);
        }

        var millis = new double[measured];
        for (var i = 0; i < measured; i++)
        {
            var request = NewRequest();
            var start = Stopwatch.GetTimestamp();
            var response = await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);
            millis[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.True(response.Success, response.Error?.Code);
        }

        Array.Sort(millis);
        double P(double q) => millis[(int)Math.Ceiling(q * millis.Length) - 1];
        var line = $"dispatch overhead, no-op handler, {replicas} replica(s), {measured} sequential calls after {warmUp} warm-up: " +
                   $"min={millis[0]:F2} p50={P(0.50):F2} p95={P(0.95):F2} p99={P(0.99):F2} max={millis[^1]:F2} ms " +
                   $"[{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, {RuntimeInformation.FrameworkDescription}]";
        output.WriteLine(line);

        // The handler does nothing, so the round trip is the dispatch overhead. Shared CI agents get a generous bound;
        // the story's target is asserted when OCTO_METHOD_PERF_STRICT=1 (quiet machine).
        var strict = Environment.GetEnvironmentVariable("OCTO_METHOD_PERF_STRICT") == "1";
        var budget = strict ? 20.0 : 250.0;
        Assert.True(P(0.95) < budget, $"p95 {P(0.95):F2} ms is not below {budget} ms. {line}");
        Assert.True(P(0.50) <= P(0.95) && P(0.95) <= P(0.99));
    }
}
