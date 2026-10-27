using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests.Adversarial;

/// <summary>
///     AB#6915 adversarial suite over the real method path, against a private RabbitMQ container (nothing of the shared
///     local stack is used). Three parties share one broker:
///     <list type="bullet">
///         <item>the <b>handler host</b>: a remote handler with caller token validation switched on (fake identity service,
///         audience configured), the way a service hosts a handler;</item>
///         <item>the <b>gateway</b>: dispatcher, authorization filter (band 200) and an audit probe (band 100), with the
///         principal of the HTTP caller in <see cref="CkMethodPrincipalScope" />;</item>
///         <item>the <b>attacker</b>: a bus participant without any filter that publishes crafted requests straight to the
///         method queue (a forged message on the unauthenticated bus).</item>
///     </list>
///     Every case asserts the documented code and that the handler did not run (no side effect), and writes its observed
///     result to <c>adversarial-results.md</c> (see <see cref="AdversarialCaseLog" />). Run with
///     <c>dotnet test tests/Methods.IntegrationTests --filter "Category=Adversarial"</c>.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "Adversarial")]
public sealed class CkMethodAdversarialBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string Tenant = AdversarialTokens.Tenant;
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";
    private const string OtherUpper = "65F0C0FFEE65F0C0FFEE65F1";
    private const string Missing = "000000000000000000000000";
    private const string Canary = "CANARY-SECRET-6915";
    private const string ClaimCanary = "CANARY-CLAIM-6915";

    private readonly string _instancePrefix = $"adv-{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<IEventHubControl> _hubs = [];
    private readonly AdversarialKey _key = new("adv-key-1");
    private readonly CaptureSink _sink = new();
    private ActivityListener? _listener;
    private AdvProbe _probe = new();
    private IDistributionEventHubService? _attacker;

    public ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _sink.Add("activity", DescribeActivity(activity))
        };
        ActivitySource.AddActivityListener(_listener);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Dispose();
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

        _key.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // handler, definition
    // ---------------------------------------------------------------------------------------------------------------

    public sealed record AdvParameters
    {
        public required string NewPassword { get; init; }

        public string? Note { get; init; }

        public string? Mode { get; init; }
    }

    public sealed record AdvResult
    {
        public string? Value { get; init; }
    }

    /// <summary>What the handler saw: the side effects of an attack.</summary>
    public sealed class AdvProbe
    {
        private int _calls;

        public int Calls => _calls;

        public CkMethodPrincipal? Principal { get; set; }

        public CkMethodCaller? Caller { get; set; }

        public string? SeenPassword { get; set; }

        public int NoteLength { get; set; }

        public void Hit() => Interlocked.Increment(ref _calls);
    }

    public sealed class AdvHandler(AdvProbe probe) : ICkMethodHandler<AdvParameters, AdvResult>
    {
        public Task<CkMethodOutcome<AdvResult>> HandleAsync(CkMethodContext<AdvParameters> context,
            CancellationToken cancellationToken)
        {
            probe.Hit();
            probe.Principal = context.CallerPrincipal;
            probe.Caller = context.Caller;
            probe.SeenPassword = context.Parameters.NewPassword;
            probe.NoteLength = context.Parameters.Note?.Length ?? 0;
            if (context.Parameters.Mode == "bogus")
            {
                // a handler that tries to answer with a code of the reserved platform prefix that is no platform code
                return Task.FromResult(CkMethodOutcome<AdvResult>.Fail("METHOD_BOGUS_CODE"));
            }

            return Task.FromResult<CkMethodOutcome<AdvResult>>(new AdvResult { Value = "changed" });
        }
    }

    private static CkMethodDto Definition(CkMethodAuthorizationDto? authorization) => new()
    {
        MethodId = "ChangePassword-1",
        Parameters =
        [
            new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "note", ValueType = AttributeValueTypesDto.String, IsOptional = true },
            new CkMethodParameterDto { Name = "mode", ValueType = AttributeValueTypesDto.String, IsOptional = true }
        ],
        Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String },
        Authorization = authorization
    };

    private sealed class DefinitionSource(CkMethodDto definition)
        : ICkMethodDefinitionSource, ICkMethodAuthorizationDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) => qualifiedMethodId == MethodId ? definition : null;

        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => Find(qualifiedMethodId);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // topology
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The remote handler host with token validation and a configured audience, plus the attacker.</summary>
    /// <summary>
    ///     The remote host. The host enforces the declared authorization (AB#7664): without an explicit one the method
    ///     declares the role the default token carries, unless <paramref name="declareNothing" /> keeps the definition
    ///     without any <c>authorization</c> (default-deny).
    /// </summary>
    private async Task StartRemoteHostAsync(CkMethodAuthorizationDto? authorization = null, bool declareNothing = false)
    {
        authorization ??= declareNothing ? null : new CkMethodAuthorizationDto { Roles = [AdversarialTokens.DefaultRole] };
        var definition = new DefinitionSource(Definition(authorization));
        var identity = new AdversarialIdentity(_key);
        var services = BaseServices();
        services.AddSingleton(_probe);
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddSingleton<IHostEnvironment>(new ProductionEnvironment());
        services.AddCkMethodCallerTokenValidation(o =>
        {
            o.Authority = AdversarialIdentity.Authority;
            o.ValidAudiences = [AdversarialTokens.Audience];
        });
        services.AddHttpClient(CkMethodTokenOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => identity);
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "HandlerA";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddCkMethodHandlers("HandlerA", m => m.Handle<AdvHandler>(MethodId));
        var host = await StartHubAsync(services);
        foreach (var hosted in host.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        var attacker = BaseServices();
        attacker.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Attacker";
            c.AutomaticallyStartBusDuringStartup = false;
        });
        var attackerProvider = await StartHubAsync(attacker);
        _attacker = attackerProvider.GetRequiredService<IDistributionEventHubService>();
    }

    /// <summary>The gateway: dispatcher with authorization (200) and audit probe (100), handler remote or in-process.</summary>
    private async Task<ICkMethodDispatcher> StartGatewayAsync(bool inProcess, CkMethodAuthorizationDto? authorization)
    {
        var definition = new DefinitionSource(Definition(authorization));
        if (!inProcess)
        {
            await StartRemoteHostAsync(authorization, declareNothing: authorization == null);
        }

        var services = BaseServices();
        services.AddSingleton(inProcess ? _probe : new AdvProbe());
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddSingleton<ICkMethodAuthorizationDefinitionSource>(definition);
        services.AddSingleton<ICkMethodInvocationFilter>(new AuditProbeFilter(_sink));
        services.AddCkMethodAuthorization();
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Gateway" + (inProcess ? "InProcess" : "Bus");
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        if (inProcess)
        {
            services.AddCkInProcessMethodHandlers(m => m.Handle<AdvHandler>(MethodId));
        }

        var provider = await StartHubAsync(services);
        return provider.GetRequiredService<ICkMethodDispatcher>();
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
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new CaptureLoggerProvider(_sink)));
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

    // ---------------------------------------------------------------------------------------------------------------
    // calls
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The Caller DTO of an attacker: claims to be an administrator.</summary>
    private static CkMethodCaller ForgedAdmin() => new()
    {
        SubjectId = AdversarialTokens.UserB,
        Roles = ["AdminPanelManagement", "AssetManagement"],
        TenantId = Tenant
    };

    private static string Params(string? note = null, string? mode = null) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["newPassword"] = Canary, ["note"] = note, ["mode"] = mode
        }.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value));

    private static CkMethodRequest Raw(string? token, CkMethodCaller? caller = null, DateTime? deadline = null,
        string? parametersJson = null, string tenant = Tenant, string target = Owner) => new(tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = MethodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = target,
        ParametersJson = parametersJson ?? Params(),
        Caller = caller ?? ForgedAdmin(),
        CallerAccessToken = token,
        DeadlineUtc = deadline ?? DateTime.UtcNow.AddSeconds(15)
    };

    /// <summary>Publishes a crafted request straight to the method queue, bypassing every filter of a gateway.</summary>
    private async Task<CkMethodResponse> SendRawAsync(CkMethodRequest request, TimeSpan? wait = null)
    {
        var timeout = wait ?? TimeSpan.FromSeconds(20);
        return await _attacker!.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(
            CkMethodQueueNames.For(request.MethodId), request,
            new CommandSendOptions { Timeout = timeout, Mandatory = true, TimeToLive = timeout, Persistent = false },
            TestContext.Current.CancellationToken);
    }

    /// <summary>A call through the gateway as the authenticated HTTP caller <paramref name="principal" />.</summary>
    private async Task<CkMethodResponse> ViaGatewayAsync(ICkMethodDispatcher dispatcher, CkMethodPrincipal? principal,
        string target = Owner, DateTime? deadline = null, string? parametersJson = null)
    {
        using (principal == null ? null : CkMethodPrincipalScope.Begin(principal))
        {
            // the gateway forwards the real token of its HTTP caller; the Caller DTO is whatever the sender wrote: here a
            // forged administrator
            var token = AdversarialTokens.Create(_key, subject: principal?.Subject ?? AdversarialTokens.UserA,
                roles: principal?.Roles.ToArray());
            return await dispatcher.InvokeAsync(Raw(token, ForgedAdmin(), deadline, parametersJson, target: target),
                TestContext.Current.CancellationToken);
        }
    }

    private static CkMethodPrincipal Caller(string sub, params string[] roles) =>
        new(sub, roles, [CkMethodAuthorizationOptions.ApiScope]);

    private static string Observed(CkMethodResponse response) =>
        response.Success
            ? "success"
            : response.Error!.Code + (response.Error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason)
                ? $" / {reason}"
                : string.Empty);

    private static void Record(string caseId, string attack, CkMethodResponse? response, AdvProbe probe,
        [CallerMemberName] string test = "")
    {
        var sideEffect = probe.Calls == 0 ? "none (handler not called)" : $"HANDLER RAN {probe.Calls}x";
        AdversarialCaseLog.Record(caseId, attack, "CkMethodAdversarialBusTests." + test,
            response == null ? "-" : Observed(response), sideEffect);
    }

    private async Task AssertRemoteRefusedAsync(string caseId, string attack, string? token, string expectedReason,
        [CallerMemberName] string test = "")
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(token));

        Record(caseId, attack, response, _probe, test);
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code); // METHOD_FORBIDDEN, not METHOD_HANDLER_FAILED
        Assert.Equal(expectedReason, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(0, _probe.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C00 control: the harness really reaches the handler
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C00_Control_AValidTokenOfTheRightTenantAndAudience_ReachesTheHandler_AsTheTokenOwner()
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key)));

        Record("C00", "control: valid token (tenant, audience, octo_api), forged Caller DTO", response, _probe);
        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(1, _probe.Calls);
        Assert.Equal(AdversarialTokens.UserA, _probe.Principal!.Subject); // from the token
        Assert.DoesNotContain("AdminPanelManagement", _probe.Principal.Roles);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C01 .. C07: the caller identity of a remote call
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task C01_ForgedCallerDto_WithoutToken_IsForbidden() =>
        AssertRemoteRefusedAsync("C01", "forged Caller DTO (administrator), no token", null, CkMethodTokenReasons.TokenMissing);

    [Fact]
    public async Task C01b_ForgedCallerDto_DoesNotEscalateAValidToken()
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key, subject: AdversarialTokens.UserA),
            ForgedAdmin()));

        Record("C01b", "forged Caller DTO (administrator) next to the valid token of a plain user", response, _probe);
        Assert.True(response.Success);
        Assert.Equal(AdversarialTokens.UserA, _probe.Principal!.Subject);
        Assert.DoesNotContain("AdminPanelManagement", _probe.Principal.Roles);
        // the DTO is informational: a handler that decided on it would trust the forgery
        Assert.Equal(AdversarialTokens.UserB, _probe.Caller!.SubjectId);
    }

    [Fact]
    public async Task C02_TokenOfAnotherTenant_IsForbidden()
    {
        await StartRemoteHostAsync();
        var cases = new[]
        {
            ("other tenant", AdversarialTokens.Create(_key, tenant: "othertenant")),
            ("parent tenant", AdversarialTokens.Create(_key, tenant: "meshtest-parent"))
        };

        foreach (var (name, token) in cases)
        {
            var response = await SendRawAsync(Raw(token));
            Record("C02", $"token of another tenant ({name})", response, _probe);
            Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
            Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        }

        // the same token for its own tenant would work, but the request names the other one
        var replayed = await SendRawAsync(Raw(AdversarialTokens.Create(_key), tenant: "othertenant"));
        Record("C02", "valid token of meshtest replayed in a request for another tenant", replayed, _probe);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, replayed.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public Task C03_ExpiredToken_IsForbidden_WithADistinctReason() =>
        AssertRemoteRefusedAsync("C03", "expired token (beyond the 30 s skew)",
            AdversarialTokens.Create(_key, expires: DateTime.UtcNow.AddMinutes(-5)), CkMethodTokenReasons.TokenExpired);

    [Fact]
    public async Task C04_WrongAudience_IsForbidden_AlsoWhenTheAudienceIsMissing()
    {
        await StartRemoteHostAsync();

        var wrong = await SendRawAsync(Raw(AdversarialTokens.Create(_key, audience: "another-api")));
        var missing = await SendRawAsync(Raw(AdversarialTokens.Create(_key, audience: null)));

        Record("C04", "token with another audience", wrong, _probe);
        Record("C04", "token without audience (audience configured)", missing, _probe);
        Assert.Equal(CkMethodTokenReasons.AudienceMismatch, wrong.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(CkMethodTokenReasons.AudienceMismatch, missing.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, wrong.Error.Code);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task C05_WrongSignature_IsForbidden()
    {
        await StartRemoteHostAsync();
        using var foreignSameKid = new AdversarialKey(_key.Kid);
        using var foreignOtherKid = new AdversarialKey("rogue-key");
        var valid = AdversarialTokens.Create(_key);
        var tampered = valid[..^6] + (valid[^6] == 'A' ? "B" : "A") + valid[^5..];
        var payloadTampered = TamperPayload(valid);

        var cases = new (string Name, string Token)[]
        {
            ("signed by another key with the same kid", AdversarialTokens.Create(foreignSameKid)),
            ("signed by an unknown key id", AdversarialTokens.Create(foreignOtherKid)),
            ("valid token, signature bytes changed", tampered),
            ("valid signature, payload changed (administrator role)", payloadTampered)
        };
        foreach (var (name, token) in cases)
        {
            var response = await SendRawAsync(Raw(token));
            Record("C05", $"wrong signature ({name})", response, _probe);
            Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
            Assert.Equal(CkMethodTokenReasons.TokenInvalid, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        }

        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task C06_AlgNone_AndKeyConfusion_AreForbidden()
    {
        await StartRemoteHostAsync();

        var none = await SendRawAsync(Raw(AdversarialTokens.Unsigned()));
        var confusion = await SendRawAsync(Raw(AdversarialTokens.SymmetricWithPublicKey(_key)));

        Record("C06", "alg: none", none, _probe);
        Record("C06", "alg HS256 signed with the public key material (key confusion)", confusion, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, none.Error!.Code);
        Assert.Equal(CkMethodTokenReasons.TokenInvalid, none.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, confusion.Error!.Code);
        Assert.Equal(CkMethodTokenReasons.TokenInvalid, confusion.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public Task C07_TokenWithoutOctoApi_IsForbidden() =>
        AssertRemoteRefusedAsync("C07", "validly signed token without scope octo_api",
            AdversarialTokens.Create(_key, scope: false), CkMethodTokenReasons.TokenInvalid);

    [Fact]
    public async Task C07b_WrongIssuer_AndOversizedToken_AreForbidden()
    {
        await StartRemoteHostAsync();

        var issuer = await SendRawAsync(Raw(AdversarialTokens.Create(_key, issuer: "https://evil.test/")));
        var oversized = await SendRawAsync(Raw(AdversarialTokens.Create(_key,
            claims: c => c["padding"] = new string('x', 64 * 1024))));

        Record("C07b", "token of another issuer", issuer, _probe);
        Record("C07b", "validly signed token larger than 32 KiB", oversized, _probe);
        Assert.Equal(CkMethodTokenReasons.TokenInvalid, issuer.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(CkMethodTokenReasons.TokenInvalid, oversized.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, _probe.Calls);
    }

    private static string TamperPayload(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[1]));
        payload = payload.Replace("\"Reporting\"", "\"AdminPanelManagement\"", StringComparison.Ordinal);
        parts[1] = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(payload);
        return string.Join('.', parts);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C08 .. C10: declared authorization through the gateway pipeline (bus and in-process)
    // ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<bool> Transports => [false, true];

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C08_AllowSelf_WithAnotherUsersId_IsRefused_AlsoInAnotherLetterCase(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, new CkMethodAuthorizationDto { AllowSelf = true });
        var transport = inProcess ? "in-process" : "bus";

        // caller is "Other"; the target is Owner. A different id in upper case is still a different id.
        var lower = await ViaGatewayAsync(dispatcher, Caller(Other), Owner);
        var upper = await ViaGatewayAsync(dispatcher, Caller(OtherUpper), Owner);
        Record("C08", $"allowSelf, another user's id ({transport})", lower, _probe);
        Record("C08", $"allowSelf, another user's id in upper case ({transport})", upper, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, lower.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, upper.Error!.Code);
        Assert.Equal(0, _probe.Calls);

        // control: the owner in another letter case passes (parsed ids), so the refusals above are not a broken harness
        var self = await ViaGatewayAsync(dispatcher, Caller(Owner.ToUpperInvariant()), Owner);
        Record("C08", $"control: allowSelf, own id in upper case ({transport})", self, _probe);
        Assert.True(self.Success, self.Error?.Code);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C09_MethodWithoutAuthorization_CalledByItsOwner_IsRefused_ForgedAdminDtoDoesNotHelp(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, authorization: null);
        var transport = inProcess ? "in-process" : "bus";

        // the owner is the target itself; the Caller DTO of the request says administrator
        var owner = await ViaGatewayAsync(dispatcher, Caller(Owner), Owner);
        var noPrincipal = await ViaGatewayAsync(dispatcher, principal: null, Owner);

        Record("C09", $"no authorization declared, called by the target's owner, forged administrator DTO ({transport})", owner, _probe);
        Record("C09", $"no authorization declared, no principal in the process, forged administrator DTO ({transport})", noPrincipal, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, owner.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, noPrincipal.Error!.Code);
        Assert.Equal(0, _probe.Calls);

        var admin = await ViaGatewayAsync(dispatcher, Caller(Other, "AdminPanelManagement"), Owner);
        Record("C09", $"control: administrator ({transport})", admin, _probe);
        Assert.True(admin.Success, admin.Error?.Code);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C10_CallerWithoutTheRight_GetsTheSameAnswer_ForAnExistingAndAMissingTarget(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        var transport = inProcess ? "in-process" : "bus";

        var existing = await ViaGatewayAsync(dispatcher, Caller(Other, "Reporting"), Owner);
        var missing = await ViaGatewayAsync(dispatcher, Caller(Other, "Reporting"), Missing);

        Record("C10", $"caller without the role, existing target ({transport})", existing, _probe);
        Record("C10", $"caller without the role, missing target ({transport})", missing, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, existing.Error!.Code);
        Assert.Equal(JsonSerializer.Serialize(existing.Error), JsonSerializer.Serialize(missing.Error));
        Assert.DoesNotContain(Owner, JsonSerializer.Serialize(existing));
        Assert.Equal(0, _probe.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C11: deadlines
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C11_DeadlineInThePast_IsTimeout_AndNotExecuted_NoMatterWhoAsks()
    {
        await StartRemoteHostAsync();
        var past = DateTime.UtcNow.AddHours(-1);

        var valid = await SendRawAsync(Raw(AdversarialTokens.Create(_key), deadline: past));
        var forged = await SendRawAsync(Raw(null, deadline: past));

        Record("C11", "deadline in the past, valid token (published on the bus)", valid, _probe);
        Record("C11", "deadline in the past, no token (published on the bus)", forged, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodTimeout, valid.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodTimeout, forged.Error!.Code); // the same answer: a message in the past teaches nothing
        Assert.Equal(0, _probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C11b_DeadlineInThePast_ThroughTheGateway_IsTimeout_AndNotExecuted(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement"), deadline: DateTime.UtcNow.AddHours(-1));

        Record("C11b", $"deadline in the past through the gateway ({(inProcess ? "in-process" : "bus")})", response, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal(0, _probe.Calls);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C11c_DeadlineAtMaxValue_ThroughTheGateway_IsClampedAndDoesNotOverflow(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement"), deadline: DateTime.MaxValue);

        Record("C11c", $"deadline DateTime.MaxValue through the gateway ({(inProcess ? "in-process" : "bus")}), authorized caller", response, _probe);
        Assert.True(response.Success, response.Error?.Code); // clamped to the platform maximum, runs normally
        var unauthorized = await ViaGatewayAsync(dispatcher, Caller(Other, "Reporting"), deadline: DateTime.MaxValue);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, unauthorized.Error!.Code);
        Assert.Equal(1, _probe.Calls);
    }

    [Fact]
    public async Task C11d_DeadlineAtMaxValue_PublishedOnTheBus_IsRefusedForAForgedMessage_AndDoesNotOverflow()
    {
        await StartRemoteHostAsync();

        var forged = await SendRawAsync(Raw(AdversarialTokens.Unsigned(), deadline: DateTime.MaxValue));
        var invalid = await SendRawAsync(Raw(null, deadline: DateTime.MaxValue));

        Record("C11d", "deadline DateTime.MaxValue published on the bus, forged token", forged, _probe);
        Record("C11d", "deadline DateTime.MaxValue published on the bus, no token", invalid, _probe);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, forged.Error!.Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, invalid.Error!.Code);
        Assert.Equal(0, _probe.Calls);
    }

    /// <summary>
    ///     A valid token with a deadline at <c>DateTime.MaxValue</c> published straight on the queue (the gateway would have
    ///     clamped it to the platform maximum): the host must not overflow or hang; it answers like any valid call.
    /// </summary>
    [Fact]
    public async Task C11e_DeadlineAtMaxValue_WithAValidToken_PublishedOnTheBus_DoesNotOverflow()
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key), deadline: DateTime.MaxValue));

        Record("C11e", "deadline DateTime.MaxValue published on the bus, valid token", response, _probe);
        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(1, _probe.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C12: parameter payload size
    // ---------------------------------------------------------------------------------------------------------------

    private const int HugeNoteBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     GAP (AB#6915 finding F-2): the method path has no limit for the size of the parameters. This characterization
    ///     test pins what is observed today, so that a later limit makes it fail and
    ///     <see cref="C12_ParametersFarAboveTheLimit_AreRefused_AndNotExecuted" /> is un-skipped.
    /// </summary>
    [Fact]
    public async Task C12_Gap_ParametersOf8MiB_AreAcceptedAndExecuted_BecauseThereIsNoSizeLimit()
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key),
            parametersJson: Params(note: new string('n', HugeNoteBytes))));

        Record("C12", "parameter payload of 8 MiB (no size limit exists) - GAP F-2", response, _probe);
        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(HugeNoteBytes, _probe.NoteLength);
    }

    [Fact(Skip = "AB#6915 finding F-2: the method path has no parameter size limit and no documented code; enable when the limit exists")]
    public async Task C12_ParametersFarAboveTheLimit_AreRefused_AndNotExecuted()
    {
        await StartRemoteHostAsync();

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key),
            parametersJson: Params(note: new string('n', HugeNoteBytes))));

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(0, _probe.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C13: reserved code prefix
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task C13_HandlerReturningAnUnknownMethodPrefixedCode_IsHandlerFailed_NeverForwarded(bool inProcess)
    {
        var dispatcher = await StartGatewayAsync(inProcess, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement"),
            parametersJson: Params(mode: "bogus"));

        AdversarialCaseLog.Record("C13", $"handler answers with code METHOD_BOGUS_CODE ({(inProcess ? "in-process" : "bus")})",
            "CkMethodAdversarialBusTests." + nameof(C13_HandlerReturningAnUnknownMethodPrefixedCode_IsHandlerFailed_NeverForwarded),
            Observed(response), "handler ran by design (authorized caller); its code is not forwarded");
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.DoesNotContain("METHOD_BOGUS_CODE", JsonSerializer.Serialize(response));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C15: replay
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C15_ValidMessageOfCallerA_ReplayedWithTheCallerDtoOfB_DoesNotRunAsB()
    {
        await StartRemoteHostAsync();
        var tokenOfA = AdversarialTokens.Create(_key, subject: AdversarialTokens.UserA);

        // the message of A, captured; the attacker swaps the Caller DTO for the administrator B and sends it again
        var original = Raw(tokenOfA, new CkMethodCaller { SubjectId = AdversarialTokens.UserA, Roles = [] });
        var replayed = original with { InvocationId = Guid.NewGuid(), Caller = ForgedAdmin() };
        var response = await SendRawAsync(replayed);

        Record("C15", "valid message of A replayed with the Caller DTO of administrator B", response, _probe);
        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(AdversarialTokens.UserA, _probe.Principal!.Subject); // never B
        Assert.NotEqual(AdversarialTokens.UserB, _probe.Principal.Subject);
        Assert.DoesNotContain("AdminPanelManagement", _probe.Principal.Roles);
    }

    [Fact]
    public async Task C15b_TokenOfA_WithTheTenantOfTheMessageChanged_IsForbidden()
    {
        await StartRemoteHostAsync();
        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key, subject: AdversarialTokens.UserA),
            tenant: "othertenant"));

        Record("C15b", "message of A replayed for another tenant", response, _probe);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, response.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, _probe.Calls);
    }

    /// <summary>
    ///     ACCEPTED LIMITATION L-1 (decision AB#7039 / OD-11, option C then B0/B1): an intact message of A, replayed
    ///     unchanged by someone who can publish on the method queue, runs again as A until the token expires. The pin is
    ///     here so that a binding of the token to the invocation (B1) turns this test red and it gets rewritten.
    /// </summary>
    [Fact]
    public async Task C15c_Limitation_AnIntactMessage_ReplayedWithinTheTokenLifetime_RunsAgainAsA()
    {
        await StartRemoteHostAsync();
        var message = Raw(AdversarialTokens.Create(_key, subject: AdversarialTokens.UserA));

        var first = await SendRawAsync(message with { InvocationId = Guid.NewGuid() });
        var second = await SendRawAsync(message with { InvocationId = Guid.NewGuid() });

        Record("C15c", "LIMITATION L-1: intact message of A published again within the token lifetime", second, _probe);
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, _probe.Calls);
        Assert.Equal(AdversarialTokens.UserA, _probe.Principal!.Subject);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C16: the remote handler host enforces the declared authorization (F-1 of AB#6915, fixed by AB#7664)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     A participant of the bus with the valid token of any user of the tenant publishes straight to the method queue
    ///     for a method whose roles the user does not hold: the host refuses with the gateway's envelope and the handler
    ///     does not run.
    /// </summary>
    [Fact]
    public async Task C16_RemoteHost_RefusesACallerWithoutTheDeclaredRole()
    {
        await StartRemoteHostAsync(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key, roles: ["Reporting"])));

        Record("C16", "token WITHOUT the declared role published on the bus (declared roles: AssetManagement)", response, _probe);
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(CkMethodAuthorizationErrors.ReasonNotAuthorized, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task C16_RemoteHost_RunsTheHandlerForACallerWithTheDeclaredRole()
    {
        await StartRemoteHostAsync(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });

        var response = await SendRawAsync(Raw(AdversarialTokens.Create(_key, roles: ["AssetManagement"])));

        Assert.True(response.Success);
        Assert.Equal(1, _probe.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // C14: canary scan over the whole path
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task C14_Canary_NeverAppearsInLogsAuditActivitiesOrResponses_OnTheWholePathIncludingTheAttacks()
    {
        var dispatcher = await StartGatewayAsync(inProcess: false, new CkMethodAuthorizationDto { Roles = ["AssetManagement"] });
        using var foreign = new AdversarialKey(_key.Kid);
        var tokens = new List<string>
        {
            AdversarialTokens.Create(_key, claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Create(_key, tenant: "othertenant", claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Create(_key, expires: DateTime.UtcNow.AddMinutes(-5), claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Create(_key, audience: "another-api", claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Create(foreign, claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Create(_key, scope: false, claims: c => c["name"] = ClaimCanary),
            AdversarialTokens.Unsigned(),
            "CANARY-GARBAGE-TOKEN-6915.not.a-jwt"
        };
        var responses = new List<CkMethodResponse>();

        // through the gateway: success, refusal, past deadline, a failing handler argument
        responses.Add(await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement")));
        responses.Add(await ViaGatewayAsync(dispatcher, Caller(Other, "Reporting")));
        responses.Add(await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement"), deadline: DateTime.UtcNow.AddHours(-1)));
        responses.Add(await ViaGatewayAsync(dispatcher, Caller(Other, "AssetManagement"), parametersJson: "{\"newPassword\":\"" + Canary));
        // straight on the bus: every attack token with a sensitive parameter
        foreach (var token in tokens)
        {
            responses.Add(await SendRawAsync(Raw(token)));
        }

        responses.Add(await SendRawAsync(Raw(AdversarialTokens.Create(_key, claims: c => c["name"] = ClaimCanary))));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        foreach (var response in responses)
        {
            _sink.Add("response", JsonSerializer.Serialize(response));
            _sink.Add("response-text", response.ToString());
        }

        var captured = _sink.Snapshot();
        Assert.Contains(captured, c => c.Kind == "audit-request"); // the scan looked at the audit view of the gateway
        Assert.Contains(captured, c => c.Kind == "log");
        Assert.Contains(captured, c => c.Kind == "activity");
        var needles = new List<string> { Canary, ClaimCanary, "CANARY-GARBAGE-TOKEN" };
        needles.AddRange(tokens);
        var leaks = captured.Where(c => needles.Any(n => c.Text.Contains(n, StringComparison.Ordinal))).ToList();
        AdversarialCaseLog.Record("C14", $"canary (parameter, token, claim value) over gateway + bus + attacks: {captured.Count} captured items (logs, audit view, activities, responses)",
            "CkMethodAdversarialBusTests." + nameof(C14_Canary_NeverAppearsInLogsAuditActivitiesOrResponses_OnTheWholePathIncludingTheAttacks),
            leaks.Count == 0 ? "no plaintext found" : $"{leaks.Count} LEAKS", "none");
        Assert.True(leaks.Count == 0,
            "canary found in: " + string.Join(" | ", leaks.Select(l => $"{l.Kind}: {Truncate(l.Text)}")));
        Assert.True(_probe.Calls >= 1); // the handler saw the raw values (the one place that needs them)
        Assert.Equal(Canary, _probe.SeenPassword);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------------------------------

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;

    private static string DescribeActivity(Activity activity) =>
        string.Join(" ", new[] { activity.DisplayName, activity.OperationName, activity.StatusDescription }
            .Concat(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"))
            .Concat(activity.Baggage.Select(b => $"{b.Key}={b.Value}"))
            .Concat(activity.Events.Select(e => e.Name + string.Join(",", e.Tags.Select(t => $"{t.Key}={t.Value}")))));

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "AdversarialHost";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>The audit filter of F3.5 stand-in: outermost, writes down everything it is allowed to see.</summary>
    private sealed class AuditProbeFilter(CaptureSink sink) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            sink.Add("audit-request", JsonSerializer.Serialize(context.Request));
            sink.Add("audit-context", context.ToString());
            var response = await next(context.Request);
            sink.Add("audit-response", JsonSerializer.Serialize(response));
            return response;
        }
    }

    private sealed record Captured(string Kind, string Text);

    private sealed class CaptureSink
    {
        private readonly ConcurrentQueue<Captured> _items = new();

        public void Add(string kind, string text) => _items.Enqueue(new Captured(kind, text));

        public List<Captured> Snapshot() => _items.ToList();
    }

    private sealed class CaptureLoggerProvider(CaptureSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogger(string category, CaptureSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            sink.Add("log", $"scope[{category}] {Describe(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add("log", $"{category} {logLevel} {formatter(state, exception)} {Describe(state)} {exception}");

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(",", pairs.Select(p => $"{p.Key}={p.Value}"))
                : state?.ToString() ?? string.Empty;
    }
}
