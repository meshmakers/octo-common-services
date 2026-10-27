using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
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
///     The bus transport of the handler SDK against a private RabbitMQ: a typed handler in one "process" (service
///     provider), the caller in another. Covers the documented sample handler end to end.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodHandlerBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly string _instancePrefix = $"methods-{Guid.NewGuid():N}";
    private readonly List<Host> _hosts = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // ---- the documented sample (docs/method-handler-sdk.md) -------------------------------------------------

    /// <summary>Shaped like the generated <c>AccountChangePasswordParameters</c>.</summary>
    public sealed record AccountChangePasswordParameters
    {
        public required string NewPassword { get; init; }
    }

    /// <summary>Shaped like the generated <c>AccountChangePasswordResult</c>.</summary>
    public sealed record AccountChangePasswordResult
    {
        public int? Value { get; init; }
    }

    public interface IAccountStore
    {
        Task<int?> SetPasswordAsync(string accountId, string newPassword, CancellationToken cancellationToken);
    }

    /// <summary>A scoped service the handler takes by constructor injection, like a repository.</summary>
    public sealed class InMemoryAccountStore : IAccountStore
    {
        public static int Calls;

        public Task<int?> SetPasswordAsync(string accountId, string newPassword, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(accountId switch
            {
                "unknown" => (int?)null,
                "boom" => throw new InvalidOperationException($"store failed for {newPassword}"),
                _ => newPassword.Length
            });
        }
    }

    /// <summary>The whole handler a service developer writes.</summary>
    public sealed class ChangePasswordHandler(IAccountStore accounts)
        : ICkMethodHandler<AccountChangePasswordParameters, AccountChangePasswordResult>
    {
        public async Task<CkMethodOutcome<AccountChangePasswordResult>> HandleAsync(
            CkMethodContext<AccountChangePasswordParameters> context, CancellationToken cancellationToken)
        {
            if (context.Parameters.NewPassword.Length < 8)
            {
                return CkMethodOutcome<AccountChangePasswordResult>.Fail("PASSWORD_TOO_SHORT", ("minLength", "8"));
            }

            var length = await accounts.SetPasswordAsync(context.TargetRtId!, context.Parameters.NewPassword,
                cancellationToken);
            return length is null
                ? CkMethodOutcome<AccountChangePasswordResult>.TargetNotFound()
                : new AccountChangePasswordResult { Value = length };
        }
    }

    // ---- hosts ------------------------------------------------------------------------------------------------

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

    private sealed class CapturedEvents
    {
        public ConcurrentQueue<CkMethodHandlerAnnounced> Announced { get; } = new();
        public ConcurrentQueue<CkMethodHandlerWithdrawn> Withdrawn { get; } = new();
    }

    private sealed class AnnouncedCapture(CapturedEvents events) : IDistributedConsumer<CkMethodHandlerAnnounced>
    {
        public Task ConsumeAsync(IDistributedContext<CkMethodHandlerAnnounced> context)
        {
            events.Announced.Enqueue(context.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class WithdrawnCapture(CapturedEvents events) : IDistributedConsumer<CkMethodHandlerWithdrawn>
    {
        public Task ConsumeAsync(IDistributedContext<CkMethodHandlerWithdrawn> context)
        {
            events.Withdrawn.Enqueue(context.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class Host(ServiceProvider provider) : IAsyncDisposable
    {
        private readonly List<IHostedService> _started = [];
        private bool _stopped;

        public ServiceProvider Provider { get; } = provider;

        public async Task StartAsync()
        {
            await Provider.GetRequiredService<IEventHubControl>().StartAsync();
            // what HostedInitializer does: run the ordered initialization services
            using (var scope = Provider.CreateScope())
            {
                foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>()
                             .OrderBy(s => s.Order))
                {
                    await service.InitializeAsync();
                }
            }

            foreach (var hosted in Provider.GetServices<IHostedService>()
                         .Where(h => h is CkMethodHandlerHeartbeatService))
            {
                await hosted.StartAsync(CancellationToken.None);
                _started.Add(hosted);
            }
        }

        /// <summary>Graceful stop: withdraw + endpoint removal (SIGTERM path).</summary>
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

    private async Task<Host> StartHandlerHostAsync(string serviceName, TimeSpan? heartbeat = null)
    {
        var services = BaseServices();
        services.AddScoped<IAccountStore, InMemoryAccountStore>();
        services.AddSingleton<ICkMethodDefinitionSource>(new DefinitionSource());
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = serviceName;
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddSingleton<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
        services.AddCkMethodHandlers(serviceName, m => m.Handle<ChangePasswordHandler>(MethodId));
        if (heartbeat is { } interval)
        {
            services.Configure<CkMethodHandlerOptions>(o => o.HeartbeatInterval = interval);
        }

        return await StartAsync(services);
    }

    private async Task<(Host Host, CapturedEvents Events)> StartCallerHostAsync()
    {
        var events = new CapturedEvents();
        var services = BaseServices();
        services.AddSingleton(events);
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "CallerTest";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddBroadcastEventConsumer<AnnouncedCapture, CkMethodHandlerAnnounced>();
            c.AddBroadcastEventConsumer<WithdrawnCapture, CkMethodHandlerWithdrawn>();
        });
        return (await StartAsync(services), events);
    }

    private async Task<Host> StartAsync(ServiceCollection services)
    {
        var host = new Host(services.BuildServiceProvider());
        _hosts.Add(host);
        await host.StartAsync();
        return host;
    }

    private static CkMethodRequest NewRequest(string target = "acc-1", string password = "correct-horse-battery") => new("meshtest")
    {
        InvocationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = target,
        ParametersJson = JsonSerializer.Serialize(new { newPassword = password }),
        DeadlineUtc = DateTime.UtcNow + CallTimeout
    };

    private static Task<CkMethodResponse> CallAsync(Host caller, CkMethodRequest request) =>
        caller.Provider.GetRequiredService<IDistributionEventHubService>()
            .GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(CkMethodQueueNames.For(request.MethodId),
                request, new CommandSendOptions { Mandatory = true, Timeout = CallTimeout, TimeToLive = CallTimeout },
                TestContext.Current.CancellationToken);

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

    // ---- tests ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invoke_TypedHandler_ReceivesTheDeserializedRecord_AndTheResultIsInTheEnvelope()
    {
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();
        var request = NewRequest(password: "correct-horse-battery");

        var response = await CallAsync(caller, request);

        Assert.True(response.Success);
        Assert.Equal(request.InvocationId, response.InvocationId);
        Assert.Equal("{\"value\":21}", response.ResultJson);
        Assert.NotEmpty(response.HandlerInstanceId);
    }

    [Fact]
    public async Task DeclaredError_TravelsAsCodeAndParameters()
    {
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();

        var response = await CallAsync(caller, NewRequest(password: "short"));

        Assert.False(response.Success);
        Assert.Equal("PASSWORD_TOO_SHORT", response.Error!.Code);
        Assert.Equal("8", response.Error.Parameters["minLength"]);
    }

    [Fact]
    public async Task PlatformError_TargetNotFound_TravelsWithTheMethodId()
    {
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();

        var response = await CallAsync(caller, NewRequest(target: "unknown"));

        Assert.Equal(CkMethodErrorCodes.MethodTargetNotFound, response.Error!.Code);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public async Task ThrowingHandler_AnswersHandlerFailed_AndIsExecutedExactlyOnce()
    {
        InMemoryAccountStore.Calls = 0;
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();

        var response = await CallAsync(caller, NewRequest(target: "boom", password: "never-on-the-wire"));

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.NotEmpty(response.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.DoesNotContain("never-on-the-wire", JsonSerializer.Serialize(response));
        // longer than the first bus retry interval (1 s): a rethrown exception would run the method again
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, InMemoryAccountStore.Calls);
    }

    [Fact]
    public async Task NewerContractVersion_IsAnsweredAsUnsupported()
    {
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();

        var response = await CallAsync(caller, NewRequest() with { ContractVersion = 2 });

        Assert.Equal(CkMethodErrorCodes.MethodContractVersionUnsupported, response.Error!.Code);
        Assert.Equal("2", response.Error.Parameters[CkMethodErrorParameters.RequestedVersion]);
        Assert.Equal("1", response.Error.Parameters[CkMethodErrorParameters.SupportedVersion]);
    }

    [Fact]
    public async Task SameInvocationTwice_ExecutedOnce_SecondIsOutcomeUnknown()
    {
        InMemoryAccountStore.Calls = 0;
        await StartHandlerHostAsync("HandlerA");
        var (caller, _) = await StartCallerHostAsync();
        var request = NewRequest();

        var first = await CallAsync(caller, request);
        var second = await CallAsync(caller, request);

        Assert.True(first.Success);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, second.Error!.Code);
        Assert.Equal(1, InMemoryAccountStore.Calls);
    }

    [Fact]
    public async Task RequestBeforeTheHandlerHost_FailsFastAsNotRoutable_AndOnceStartedItIsDelivered()
    {
        var (caller, events) = await StartCallerHostAsync();

        // nobody consumes the method queue yet: no METHOD_TIMEOUT, a typed failure within the timeout
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<Meshmakers.Octo.Common.DistributionEventHub.CommandNotRoutableException>(() => CallAsync(caller, NewRequest()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"not routable took {sw.Elapsed}");
        Assert.Empty(events.Announced);

        // StartAsync returns only after every endpoint is ready and the handler is announced
        var handler = await StartHandlerHostAsync("HandlerA");
        var instanceId = handler.Provider.GetRequiredService<CkMethodHandlerInstance>().InstanceId;
        await WaitUntilAsync(() => events.Announced.Any(a => a.InstanceId == instanceId), TimeSpan.FromSeconds(5));

        // a request sent right after the announcement is accepted by the endpoint: never racing the queue creation
        var response = await CallAsync(caller, NewRequest());
        Assert.True(response.Success);
    }

    [Fact]
    public async Task HandlerIsAnnounced_Heartbeats_AnswersDiscovery_AndWithdrawsOnGracefulStop()
    {
        var (caller, events) = await StartCallerHostAsync();
        var handler = await StartHandlerHostAsync("HandlerA", heartbeat: TimeSpan.FromSeconds(1));
        var instanceId = handler.Provider.GetRequiredService<CkMethodHandlerInstance>().InstanceId;

        // announced after start
        await WaitUntilAsync(() => events.Announced.Any(a => a.InstanceId == instanceId), TimeSpan.FromSeconds(5));
        var announced = events.Announced.First(a => a.InstanceId == instanceId);
        Assert.Equal("HandlerA", announced.ServiceName);
        Assert.Equal([MethodId], announced.MethodIds);
        Assert.Equal(CkMethodContract.CurrentVersion, announced.ContractVersion);

        // heartbeats
        await WaitUntilAsync(() => events.Announced.Count(a => a.InstanceId == instanceId) >= 3, TimeSpan.FromSeconds(6));

        // discovery request is answered at once
        var before = events.Announced.Count(a => a.InstanceId == instanceId);
        await caller.Provider.GetRequiredService<IDistributionEventHubService>().PublishAsync(
            new CkMethodHandlerDiscoveryRequested(Guid.NewGuid(), DateTime.UtcNow) { RequestingInstanceId = "test" },
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => events.Announced.Count(a => a.InstanceId == instanceId) > before,
            TimeSpan.FromMilliseconds(900));

        // graceful stop withdraws and stops announcing
        await handler.StopGracefullyAsync();
        await WaitUntilAsync(() => events.Withdrawn.Any(w => w.InstanceId == instanceId), TimeSpan.FromSeconds(3));
        var afterWithdraw = events.Announced.Count(a => a.InstanceId == instanceId);
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        Assert.Equal(afterWithdraw, events.Announced.Count(a => a.InstanceId == instanceId));
    }
}
