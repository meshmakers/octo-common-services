using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     The declared method authorization (AB#6911) through the real pipeline against a private RabbitMQ: the gateway runs
///     audit (100), authorization (200) and a stub idempotency filter (300), registered in a scrambled order, in front of a
///     handler that is reached over the bus in one case and hosted in-process in the other. The same cases run for both
///     transports.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodAuthorizationBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string Tenant = "meshtest";
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string OwnerUpper = "65F0C0FFEE65F0C0FFEE65F0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly string _instancePrefix = $"authz-{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<IEventHubControl> _hubs = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var hub in _hubs)
        {
            try
            {
                await hub.StopAsync();
            }
            catch
            {
                // best effort
            }
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    public sealed record AccountChangePasswordParameters
    {
        public required string NewPassword { get; init; }
    }

    public sealed record AccountChangePasswordResult
    {
        public int? Value { get; init; }
    }

    public sealed class HandlerProbe
    {
        public int Calls;
    }

    public sealed class ChangePasswordHandler(HandlerProbe probe)
        : ICkMethodHandler<AccountChangePasswordParameters, AccountChangePasswordResult>
    {
        public Task<CkMethodOutcome<AccountChangePasswordResult>> HandleAsync(
            CkMethodContext<AccountChangePasswordParameters> context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Calls);
            return Task.FromResult<CkMethodOutcome<AccountChangePasswordResult>>(
                new AccountChangePasswordResult { Value = context.Parameters.NewPassword.Length });
        }
    }

    private static CkMethodDto Definition(CkMethodAuthorizationDto? authorization) => new()
    {
        MethodId = "ChangePassword-1",
        Parameters = [new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true }],
        Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int },
        Authorization = authorization
    };

    private sealed class DefinitionSource(CkMethodDto definition) : ICkMethodDefinitionSource, ICkMethodAuthorizationDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) => qualifiedMethodId == MethodId ? definition : null;

        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => Find(qualifiedMethodId);
    }

    /// <summary>Audit (100) or the stub idempotency (300): records when it runs and what it saw come back.</summary>
    private sealed class TraceFilter(int order, string name, List<string> trace) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            lock (trace)
            {
                trace.Add(name + ">");
            }

            var response = await next(context.Request);
            lock (trace)
            {
                trace.Add($"{name}< {(response.Success ? "ok" : response.Error!.Code)}");
            }

            return response;
        }
    }

    private async Task<(ICkMethodDispatcher Dispatcher, List<string> Trace, HandlerProbe Probe)> StartAsync(
        bool inProcess, CkMethodAuthorizationDto? authorization)
    {
        var trace = new List<string>();
        var probe = new HandlerProbe();
        var definition = new DefinitionSource(Definition(authorization));

        if (!inProcess)
        {
            await StartHandlerHostAsync(probe, definition);
        }

        var services = BaseServices();
        services.AddSingleton(inProcess ? probe : new HandlerProbe());
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddSingleton<ICkMethodAuthorizationDefinitionSource>(definition);
        // scrambled: innermost first, authorization in the middle, outermost last
        services.AddSingleton<ICkMethodInvocationFilter>(new TraceFilter(CkMethodInvocationFilterOrder.Idempotency, "idempotency", trace));
        services.AddCkMethodAuthorization();
        services.AddSingleton<ICkMethodInvocationFilter>(new TraceFilter(CkMethodInvocationFilterOrder.Audit, "audit", trace));
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Gateway" + (inProcess ? "InProcess" : "Bus");
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        if (inProcess)
        {
            services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(MethodId));
        }

        var provider = await StartHubAsync(services);
        return (provider.GetRequiredService<ICkMethodDispatcher>(), trace, probe);
    }

    private async Task StartHandlerHostAsync(HandlerProbe probe, DefinitionSource definition)
    {
        var services = BaseServices();
        services.AddSingleton(probe);
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "HandlerA";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddCkMethodHandlers("HandlerA", m => m.Handle<ChangePasswordHandler>(MethodId));
        var provider = await StartHubAsync(services);
        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private async Task<ServiceProvider> StartHubAsync(ServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var hub = provider.GetRequiredService<IEventHubControl>();
        _hubs.Add(hub);
        await hub.StartAsync();
        using var scope = provider.CreateScope();
        foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
        {
            await service.InitializeAsync();
        }

        return provider;
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

    private static CkMethodRequest NewRequest(string target = Owner) => new(Tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = target,
        ParametersJson = JsonSerializer.Serialize(new { newPassword = "correct-horse-battery" }),
        DeadlineUtc = DateTime.UtcNow + CallTimeout
    };

    private static async Task<CkMethodResponse> CallAsync(ICkMethodDispatcher dispatcher, CkMethodPrincipal? caller,
        string target = Owner)
    {
        using (caller == null ? null : CkMethodPrincipalScope.Begin(caller))
        {
            return await dispatcher.InvokeAsync(NewRequest(target), TestContext.Current.CancellationToken);
        }
    }

    private static CkMethodPrincipal Caller(string sub = Other, params string[] roles) =>
        new(sub, roles, [CkMethodAuthorizationOptions.ApiScope]);

    public static TheoryData<bool> Transports => [false, true];

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Role_CallerWithTheRolePasses_WithoutItIsRefused_AndTheHandlerIsNotCalled(bool inProcess)
    {
        var (dispatcher, _, probe) = await StartAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var allowed = await CallAsync(dispatcher, Caller(Other, "assetmanagement"));
        Assert.True(allowed.Success, allowed.Error?.Code);
        Assert.Equal("{\"value\":21}", allowed.ResultJson);
        Assert.Equal(1, probe.Calls);

        var refused = await CallAsync(dispatcher, Caller(Other, "Other"));
        Assert.False(refused.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, refused.Error!.Code);
        Assert.Equal(string.Empty, refused.HandlerInstanceId);

        // longer than a bus hop: a request that slipped through would have run the handler by now
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task FilterOrder_IsAuditAuthorizationIdempotency_AndARefusedCallStopsAtAuthorization(bool inProcess)
    {
        var (dispatcher, trace, probe) = await StartAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["R"] });

        Assert.True((await CallAsync(dispatcher, Caller(Other, "R"))).Success);
        Assert.Equal(["audit>", "idempotency>", "idempotency< ok", "audit< ok"], trace.ToArray());

        trace.Clear();
        var refused = await CallAsync(dispatcher, Caller(Other, "Nope"));
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, refused.Error!.Code);
        Assert.Equal(["audit>", $"audit< {CkMethodErrorCodes.MethodForbidden}"], trace.ToArray()); // the audit saw the refusal
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task AllowSelf_PassesAcrossLetterCase_AnotherCallerIsRefused(bool inProcess)
    {
        var (dispatcher, _, probe) = await StartAsync(inProcess, new CkMethodAuthorizationDto { AllowSelf = true });

        Assert.True((await CallAsync(dispatcher, Caller(OwnerUpper), Owner)).Success);
        Assert.True((await CallAsync(dispatcher, Caller(Owner), OwnerUpper)).Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, (await CallAsync(dispatcher, Caller(Other), Owner)).Error!.Code);
        Assert.Equal(2, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task DefaultDeny_OnlyAnAdministratorPasses_TheOwnerIsRefused(bool inProcess)
    {
        var (dispatcher, _, probe) = await StartAsync(inProcess, authorization: null);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, (await CallAsync(dispatcher, Caller(Owner), Owner)).Error!.Code);
        Assert.Equal(0, probe.Calls);
        Assert.True((await CallAsync(dispatcher, Caller(Other, "AdminPanelManagement"), Owner)).Success);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Scopes_AreAllRequired_AndOctoApiAlways(bool inProcess)
    {
        var (dispatcher, _, probe) = await StartAsync(inProcess,
            new CkMethodAuthorizationDto { Roles = ["R"], Scopes = ["accounts:write"] });

        Assert.True((await CallAsync(dispatcher, new CkMethodPrincipal(Other, ["R"], ["octo_api", "accounts:write"]))).Success);
        Assert.False((await CallAsync(dispatcher, new CkMethodPrincipal(Other, ["R"], ["octo_api"]))).Success);
        Assert.False((await CallAsync(dispatcher, new CkMethodPrincipal(Other, ["R"], ["accounts:write"]))).Success);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task NoPrincipalInTheProcess_IsRefused_AndTheRefusalIsTheSameForAnyTarget(bool inProcess)
    {
        var (dispatcher, _, probe) = await StartAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["R"], AllowSelf = true });

        var existing = await CallAsync(dispatcher, caller: null, Owner);
        var missing = await CallAsync(dispatcher, caller: null, "000000000000000000000000");

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, existing.Error!.Code);
        Assert.Equal(JsonSerializer.Serialize(existing.Error), JsonSerializer.Serialize(missing.Error));
        Assert.Equal(0, probe.Calls);
    }
}
