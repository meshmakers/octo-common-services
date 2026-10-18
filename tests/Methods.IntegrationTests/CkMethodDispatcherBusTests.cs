using System.Diagnostics;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     The dispatcher (AB#6905) against a private RabbitMQ: a handler host in one "process" (service provider), the
///     gateway with the dispatcher in others. The same cases run through a gateway that reaches the handler over the bus
///     and through a gateway that hosts the handler in-process.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodDispatcherBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string DerivedMethodId = "Sample.Accounts/AdminAccount.ChangePassword-1";
    private const string UnregisteredMethodId = "Sample.Accounts/Account.Unregistered-1";
    private const string Tenant = "meshtest";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly string _instancePrefix = $"dispatch-{Guid.NewGuid():N}";
    private readonly List<Host> _hosts = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // ---- handler and probes -----------------------------------------------------------------------------------

    public sealed record AccountChangePasswordParameters
    {
        public required string NewPassword { get; init; }
    }

    public sealed record AccountChangePasswordResult
    {
        public int? Value { get; init; }
    }

    /// <summary>What the handler observed, per host.</summary>
    public sealed class HandlerProbe
    {
        public int Calls;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>target "acc-1" succeeds, "boom" throws, "slow" never finishes, "gate" waits until released.</summary>
    public sealed class ChangePasswordHandler(HandlerProbe probe)
        : ICkMethodHandler<AccountChangePasswordParameters, AccountChangePasswordResult>
    {
        public async Task<CkMethodOutcome<AccountChangePasswordResult>> HandleAsync(
            CkMethodContext<AccountChangePasswordParameters> context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Calls);
            if (context.Parameters.NewPassword.Length < 8)
            {
                return CkMethodOutcome<AccountChangePasswordResult>.Fail("PASSWORD_TOO_SHORT", ("minLength", "8"));
            }

            switch (context.TargetRtId)
            {
                case "boom":
                    throw new InvalidOperationException($"store failed for {context.Parameters.NewPassword}");
                case "slow":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
                case "gate":
                    probe.Entered.TrySetResult();
                    await probe.Release.Task.WaitAsync(cancellationToken);
                    break;
            }

            return new AccountChangePasswordResult { Value = context.Parameters.NewPassword.Length };
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
                Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int },
                Errors = [new CkMethodErrorDto { Code = "PASSWORD_TOO_SHORT" }]
            };
    }

    /// <summary>Records the transport of every pipeline run (one filter, registered once per gateway).</summary>
    public sealed class RecordingFilter : ICkMethodInvocationFilter
    {
        public List<string> Transports { get; } = [];

        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            lock (Transports)
            {
                Transports.Add(context.Transport);
            }

            return await next(context.Request);
        }
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

        public async Task StopGracefullyAsync()
        {
            foreach (var hosted in _started)
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            _started.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            try
            {
                await StopGracefullyAsync();
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

    private async Task<(Host Host, HandlerProbe Probe)> StartHandlerHostAsync(string serviceName = "HandlerA",
        TimeSpan? heartbeat = null)
    {
        var probe = new HandlerProbe();
        var services = BaseServices();
        services.AddSingleton(probe);
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = serviceName;
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddCkMethodHandlers(serviceName, m => m.Handle<ChangePasswordHandler>(MethodId));
        if (heartbeat is { } interval)
        {
            services.Configure<CkMethodHandlerOptions>(o => o.HeartbeatInterval = interval);
        }

        return (await StartAsync(services), probe);
    }

    /// <summary>A gateway: dispatcher + registry feed. With <paramref name="inProcess" /> it also hosts the handler.</summary>
    private async Task<(Host Host, ICkMethodDispatcher Dispatcher, RecordingFilter Filter)> StartGatewayAsync(
        bool inProcess = false, ICkCacheService? ckCache = null, string name = "Gateway")
    {
        var filter = new RecordingFilter();
        var services = BaseServices();
        services.AddSingleton(new HandlerProbe());
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddSingleton<ICkMethodInvocationFilter>(filter);
        if (ckCache != null)
        {
            services.AddSingleton(ckCache);
        }

        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = name + (inProcess ? "InProcess" : "Bus");
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        if (inProcess)
        {
            services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(MethodId));
        }

        var host = await StartAsync(services);
        return (host, host.Provider.GetRequiredService<ICkMethodDispatcher>(), filter);
    }

    private async Task<Host> StartAsync(ServiceCollection services)
    {
        var host = new Host(services.BuildServiceProvider());
        _hosts.Add(host);
        await host.StartAsync();
        return host;
    }

    private static CkMethodRequest NewRequest(string target = "acc-1", string password = "correct-horse-battery",
        string methodId = MethodId, string ckTypeId = "Sample.Accounts/Account", TimeSpan? timeout = null) => new(Tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = methodId,
        CkTypeId = ckTypeId,
        TargetRtId = target,
        ParametersJson = JsonSerializer.Serialize(new { newPassword = password }),
        DeadlineUtc = DateTime.UtcNow + (timeout ?? CallTimeout)
    };

    private static string Normalized(CkMethodResponse response) =>
        JsonSerializer.Serialize(response with { HandlerInstanceId = "<instance>" }, Wire);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"Condition not met within {timeout}");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static ICkCacheService FleetCache()
    {
        // AdminAccount derives from Account; the engine puts the inherited method into AllMethods with the base type as
        // its declaring type (octo-construction-kit-engine CkV2MethodResolverTests).
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        var method = new CkMethodGraph(new CkId<CkTypeId>("Sample.Accounts-1.0.0/Account"),
            new CkMethodDto { MethodId = "ChangePassword-1" });
        var admin = new CkTypeGraph(new CkId<CkTypeId>("Sample.Accounts-1.0.0/AdminAccount"), false, false, false, [], null,
            null, [], [], new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [],
            new CkGraphDirectedAssociations([]), "", false,
            allMethods: new Dictionary<string, CkMethodGraph> { ["ChangePassword-1"] = method });
        var rtId = new RtCkId<CkTypeId>("Sample.Accounts/AdminAccount");
        A.CallTo(() => cache.TryGetRtCkType(Tenant, rtId, out admin)).Returns(true);
        return cache;
    }

    // ---- tests ------------------------------------------------------------------------------------------------

    public static TheoryData<string> Cases => ["success", "declared-error", "thrown", "not-available"];

    private static CkMethodRequest Case(string name) => name switch
    {
        "success" => NewRequest(),
        "declared-error" => NewRequest(password: "short"),
        "thrown" => NewRequest(target: "boom", password: "never-on-the-wire"),
        "not-available" => NewRequest(methodId: UnregisteredMethodId),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SameCase_GivesEqualEnvelopes_OverTheBusAndInProcess_AndTheFilterRanOncePerCall(string name)
    {
        await StartHandlerHostAsync();
        var (_, bus, busFilter) = await StartGatewayAsync();
        var (_, inProcess, inProcessFilter) = await StartGatewayAsync(inProcess: true);
        var request = Case(name);

        var overBus = await bus.InvokeAsync(request, TestContext.Current.CancellationToken);
        var local = await inProcess.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(Normalized(overBus), Normalized(local));
        Assert.Equal(request.InvocationId, local.InvocationId);
        Assert.DoesNotContain("never-on-the-wire", JsonSerializer.Serialize(local, Wire));
        switch (name)
        {
            case "success":
                Assert.True(local.Success);
                Assert.Equal("{\"value\":21}", local.ResultJson);
                Assert.NotEmpty(overBus.HandlerInstanceId);
                Assert.NotEmpty(local.HandlerInstanceId);
                Assert.NotEqual(overBus.HandlerInstanceId, local.HandlerInstanceId); // different hosts, same envelope
                break;
            case "declared-error":
                Assert.Equal("PASSWORD_TOO_SHORT", local.Error!.Code);
                break;
            case "thrown":
                Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, local.Error!.Code);
                Assert.Equal(request.CorrelationId.ToString(), local.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
                break;
            case "not-available":
                Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, local.Error!.Code);
                Assert.Equal(UnregisteredMethodId, local.Error.Parameters[CkMethodErrorParameters.MethodId]);
                break;
        }

        // one pipeline run per call, on the transport the call took: the bus gateway always "bus"; the in-process
        // gateway "in-process" for the method it hosts and "bus" for the one it does not
        Assert.Equal([CkMethodTransportNames.Bus], busFilter.Transports);
        Assert.Equal([name == "not-available" ? CkMethodTransportNames.Bus : CkMethodTransportNames.InProcess],
            inProcessFilter.Transports);
    }

    [Fact]
    public async Task Bus_ThrowingHandler_RunsExactlyOnce_AndTheCallerGetsHandlerFailed()
    {
        var (_, probe) = await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var response = await dispatcher.InvokeAsync(NewRequest(target: "boom"), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        // longer than the first bus retry interval: a re-sent or retried request would run the handler again
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task NoConsumerEver_IsNotAvailable_WithinOneSecond_AndNamesTheMethod()
    {
        var (_, dispatcher, _) = await StartGatewayAsync();
        await dispatcher.InvokeAsync(NewRequest(methodId: UnregisteredMethodId), TestContext.Current.CancellationToken); // warm up

        var sw = Stopwatch.StartNew();
        var response = await dispatcher.InvokeAsync(NewRequest(methodId: UnregisteredMethodId),
            TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(UnregisteredMethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(string.Empty, response.HandlerInstanceId);
        Assert.DoesNotContain("   at ", JsonSerializer.Serialize(response, Wire)); // no stack trace
    }

    [Fact]
    public async Task ConsumerDisposed_IsNotAvailable_WithinOneSecond()
    {
        var (handler, _) = await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();
        Assert.True((await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken)).Success);

        await handler.DisposeAsync(); // withdraws, removes the endpoints, stops the bus
        var sw = Stopwatch.StartNew();
        var response = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public async Task HandlerWithdrawnMidFlight_TheInFlightCallStillGetsExactlyOneEnvelope_AndNewCallsFailFast()
    {
        var (handler, probe) = await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var inFlight = dispatcher.InvokeAsync(NewRequest(target: "gate", timeout: TimeSpan.FromSeconds(6)),
            TestContext.Current.CancellationToken);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await handler.StopGracefullyAsync(); // withdraw + endpoints removed while the handler is running
        var sw = Stopwatch.StartNew();
        var fresh = await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"new call took {sw.Elapsed}");
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, fresh.Error!.Code);

        probe.Release.TrySetResult();
        var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // never an exception, never lost: the handler finished (success) or the call ended at its deadline (timeout)
        Assert.True(response.Success || response.Error!.Code == CkMethodErrorCodes.MethodTimeout,
            JsonSerializer.Serialize(response, Wire));
        Assert.Equal(1, probe.Calls); // the fresh call never reached a handler
    }

    [Fact]
    public async Task BrokerRedelivery_AfterTheConsumerConnectionDropped_IsNotExecuted_AndAnsweredOutcomeUnknown()
    {
        // two instances share the queue: when one loses its connection, the broker redelivers to the other
        var (_, probeA) = await StartHandlerHostAsync("HandlerA");
        var (_, probeB) = await StartHandlerHostAsync("HandlerB");
        var (_, dispatcher, _) = await StartGatewayAsync();

        var call = dispatcher.InvokeAsync(NewRequest(target: "gate", timeout: TimeSpan.FromSeconds(30)),
            TestContext.Current.CancellationToken);
        await Task.WhenAny(probeA.Entered.Task, probeB.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var (busy, idle) = probeA.Entered.Task.IsCompleted ? (probeA, probeB) : (probeB, probeA);

        // the handler has the message but has not acknowledged it: its broker connection is lost
        await CloseConnectionWithUnacknowledgedMessageAsync();
        var response = await call.WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, response.Error!.Code);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(1, busy.Calls);
        Assert.Equal(0, idle.Calls); // the redelivery did not run the handler again
        busy.Release.TrySetResult(); // the first execution ends; its answer cannot be delivered and is swallowed
    }

    [Fact]
    public async Task TimeoutOfTheMethod_ReachesTheCaller_AsTimeoutSeconds_FromTheHandlerHost()
    {
        await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var sw = Stopwatch.StartNew();
        var response = await dispatcher.InvokeAsync(
            NewRequest(target: "slow", timeout: TimeSpan.FromSeconds(2)) with { TimeoutSeconds = 2 },
            TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal("2", response.Error.Parameters[CkMethodErrorParameters.TimeoutSeconds]); // not the host default 15
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2 + 2), $"took {sw.Elapsed}");
        // the handler host itself answered at the deadline, not the dispatcher after its grace
        Assert.NotEqual(string.Empty, response.HandlerInstanceId);
    }

    [Fact]
    public async Task RequestArrivingAfterItsDeadline_IsNotExecuted_AndAnsweredTimeout_ByTheHandlerHost()
    {
        var (_, probe) = await StartHandlerHostAsync();
        var (gateway, _, _) = await StartGatewayAsync();
        var request = NewRequest() with { DeadlineUtc = DateTime.UtcNow.AddSeconds(-30), TimeoutSeconds = 5 };

        // a sender that is not the dispatcher (the dispatcher never sends an expired request)
        var response = await gateway.Provider.GetRequiredService<IDistributionEventHubService>()
            .GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(CkMethodQueueNames.For(request.MethodId), request,
                new CommandSendOptions { Mandatory = true, Timeout = CallTimeout, TimeToLive = CallTimeout },
                TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal("5", response.Error.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
        Assert.NotEqual(string.Empty, response.HandlerInstanceId);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task DeadlineMaxValue_FromANonDispatcherSender_IsServedByTheHandlerHost_AndItsInvocationIdIsRemembered()
    {
        var (_, probe) = await StartHandlerHostAsync();
        var (gateway, _, _) = await StartGatewayAsync();
        var request = NewRequest() with { DeadlineUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) };
        var hub = gateway.Provider.GetRequiredService<IDistributionEventHubService>();
        Task<CkMethodResponse> Send() => hub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(
            CkMethodQueueNames.For(request.MethodId), request,
            new CommandSendOptions { Mandatory = true, Timeout = CallTimeout, TimeToLive = CallTimeout },
            TestContext.Current.CancellationToken);

        var first = await Send();
        var second = await Send(); // same invocation id, same instance

        Assert.True(first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>Closes the broker connection of the consumer that holds an unacknowledged message (a lost connection).</summary>
    private async Task CloseConnectionWithUnacknowledgedMessageAsync()
    {
        var channels = await rabbitMq.RabbitMqCtlAsync("list_channels", "connection", "messages_unacknowledged");
        var connectionPid = channels.Select(l => l.Split('\t'))
            .First(p => p.Length >= 2 && int.TryParse(p[1], out var unacked) && unacked > 0)[0];
        await rabbitMq.RabbitMqCtlAsync("close_connection", connectionPid, "AB6907-redelivery-test");
    }

    [Fact]
    public async Task HandlerThatDoesNotFinish_AnswersMethodTimeoutAtTheDeadline()
    {
        await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var sw = Stopwatch.StartNew();
        var response = await dispatcher.InvokeAsync(NewRequest(target: "slow", timeout: TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1.5) && sw.Elapsed < TimeSpan.FromSeconds(2 + 2 + 1),
            $"took {sw.Elapsed}"); // deadline + documented tolerance (ResponseGrace 2 s)
    }

    [Fact]
    public async Task DeadlineInThePast_IsTimeoutWithoutReachingTheHandler()
    {
        var (_, probe) = await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var response = await dispatcher.InvokeAsync(NewRequest(timeout: TimeSpan.FromSeconds(-5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task AbsurdDeadline_DoesNotThrow_AndIsServed()
    {
        await StartHandlerHostAsync();
        var (_, dispatcher, _) = await StartGatewayAsync();

        var response = await dispatcher.InvokeAsync(NewRequest() with { DeadlineUtc = DateTime.MaxValue },
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task MethodCalledThroughADerivedType_ReachesTheHandlerOfTheDeclaringType()
    {
        await StartHandlerHostAsync();
        var (_, dispatcher, filter) = await StartGatewayAsync(ckCache: FleetCache());

        var response = await dispatcher.InvokeAsync(
            NewRequest(methodId: DerivedMethodId, ckTypeId: "Sample.Accounts/AdminAccount"),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success, JsonSerializer.Serialize(response, Wire));
        Assert.Equal("{\"value\":21}", response.ResultJson);
        Assert.Single(filter.Transports);
    }

    [Fact]
    public async Task Registry_FollowsAnnounceHeartbeatAndWithdraw_AndIsFilledByDiscoveryAtStart()
    {
        var (handler, _) = await StartHandlerHostAsync(heartbeat: TimeSpan.FromSeconds(1));
        // the gateway starts AFTER the handler: only the discovery request can tell it about the handler
        var (gateway, _, _) = await StartGatewayAsync();
        var registry = gateway.Provider.GetRequiredService<ICkMethodHandlerRegistry>();

        await WaitUntilAsync(() => registry.GetLiveHandlers(MethodId).Count == 1, TimeSpan.FromMilliseconds(900));
        var first = registry.GetLiveHandlers(MethodId)[0];
        Assert.Equal("HandlerA", first.ServiceName);

        // a heartbeat refreshes the entry
        await WaitUntilAsync(() => registry.GetLiveHandlers(MethodId)[0].LastSeenUtc > first.LastSeenUtc,
            TimeSpan.FromSeconds(4));

        await handler.StopGracefullyAsync();
        await WaitUntilAsync(() => registry.GetLiveHandlers(MethodId).Count == 0, TimeSpan.FromSeconds(3));
        Assert.True(registry.GetLastSeen(MethodId)!.Withdrawn);
    }

    [Fact]
    public async Task TwoHandlerInstances_BothServeTheQueue_AndOneStopping_LeavesTheOther()
    {
        var (a, probeA) = await StartHandlerHostAsync("HandlerA");
        var (_, probeB) = await StartHandlerHostAsync("HandlerA");
        var (_, dispatcher, _) = await StartGatewayAsync();

        for (var i = 0; i < 20; i++)
        {
            Assert.True((await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken)).Success);
        }

        Assert.Equal(20, probeA.Calls + probeB.Calls);
        Assert.True(probeA.Calls > 0 && probeB.Calls > 0, $"A={probeA.Calls} B={probeB.Calls}");

        await a.DisposeAsync();
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await dispatcher.InvokeAsync(NewRequest(), TestContext.Current.CancellationToken)).Success);
        }

        Assert.Equal(25, probeA.Calls + probeB.Calls);
    }
}
