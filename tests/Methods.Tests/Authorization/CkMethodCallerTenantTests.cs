using System.Security.Claims;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     AB#8711: every in-process CK method call whose caller token was issued for another tenant is refused with
///     <c>METHOD_FORBIDDEN</c> (reason <c>tenant-not-allowed</c>) before the handler runs, with the rule of the bus host and
///     regardless of the mode of the HTTP tenant gate. The bus path is not touched.
/// </summary>
public class CkMethodCallerTenantTests
{
    private const string CallTenant = "meshtest";
    private const string OtherTenant = "octosystem";
    private const string AllowedService = "allowed-svc";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- the rule (shared with the bus host)

    public static TheoryData<string?, string?, string?, string> RuleCases => new()
    {
        // user tokens (with sub): only the call's tenant, compared case-insensitively
        { "user-1", null, CallTenant, nameof(CkMethodCallerTenantVerdict.Allowed) },
        { "user-1", null, "MeshTest", nameof(CkMethodCallerTenantVerdict.Allowed) },
        { "user-1", null, OtherTenant, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "user-1", null, null, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        // a user token is never widened by the service allow-list
        { "user-1", AllowedService, OtherTenant, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        // service tokens (no sub): the call's tenant, or a client on the allow-list
        { null, "some-svc", CallTenant, nameof(CkMethodCallerTenantVerdict.Allowed) },
        { null, "some-svc", OtherTenant, nameof(CkMethodCallerTenantVerdict.ServiceTenantMismatch) },
        { null, "some-svc", null, nameof(CkMethodCallerTenantVerdict.ServiceTenantMismatch) },
        { null, AllowedService, OtherTenant, nameof(CkMethodCallerTenantVerdict.Allowed) },
        { null, AllowedService, null, nameof(CkMethodCallerTenantVerdict.Allowed) },
        // neither sub nor client_id: cannot be attributed
        { null, null, CallTenant, nameof(CkMethodCallerTenantVerdict.NoIdentity) }
    };

    [Theory]
    [MemberData(nameof(RuleCases))]
    public void TheRule_IsTheOneOfTheBusHost(string? subjectId, string? clientId, string? tokenTenant,
        string expected)
    {
        var options = new TenantAuthorizationOptions();
        options.CrossTenantServiceClientIds.Add(AllowedService);

        Assert.Equal(Enum.Parse<CkMethodCallerTenantVerdict>(expected), CkMethodCallerTenantRule.Evaluate(subjectId, clientId, tokenTenant, CallTenant, options));
    }

    [Fact]
    public void FromClaimsPrincipal_CarriesTheTokenTenant()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(User(OtherTenant));

        Assert.True(principal.IsFromAccessToken);
        Assert.Equal(OtherTenant, principal.TokenTenantId);
        Assert.Null(principal.TenantId); // only the remote token validation sets the validated tenant

        var built = new CkMethodPrincipal("job", ["Administrators"], [CkMethodAuthorizationOptions.ApiScope]);
        Assert.False(built.IsFromAccessToken);
        Assert.Null(built.TokenTenantId);
    }

    // ---------------------------------------------------------------- the in-process transport, HTTP gate in LogOnly

    [Fact]
    public async Task AUserTokenOfAnotherTenant_IsRefusedBeforeTheHandler_WithTheHttpGateInLogOnly()
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await host.CallAsync(CkMethodPrincipal.FromClaimsPrincipal(User(OtherTenant)));

        AssertTenantRefusal(response);
        Assert.Equal(0, handler.Calls);
        // the audit filter (outermost) records the refusal, once
        var record = Assert.Single(host.Audit.Records);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, record.Outcome);
        Assert.Equal(CkMethodTransportNames.InProcess, record.Transport);
        // the log names the tenant and a fixed category, never the token or a claim value of it
        var log = host.AllLogText();
        Assert.Contains(CkMethodTokenReasons.TenantNotAllowed, log);
        Assert.Contains(nameof(CkMethodCallerTenantVerdict.UserTenantMismatch), log);
        Assert.DoesNotContain(TestData.Token, log);
        Assert.DoesNotContain(OtherTenant, log);
    }

    [Fact]
    public async Task AServiceTokenOfAnotherTenant_NotOnTheAllowList_IsRefused()
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await host.CallAsync(CkMethodPrincipal.FromClaimsPrincipal(Service("foreign-svc", OtherTenant)));

        AssertTenantRefusal(response);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AUserTokenOfTheCallsTenant_Runs()
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await host.CallAsync(CkMethodPrincipal.FromClaimsPrincipal(User("MESHTEST")));

        Assert.True(response.Success, Json(response));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(OtherTenant)]
    [InlineData(null)]
    public async Task AnAllowListedServiceToken_KeepsWorkingAcrossTenants(string? tokenTenant)
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await host.CallAsync(CkMethodPrincipal.FromClaimsPrincipal(Service(AllowedService, tokenTenant)));

        Assert.True(response.Success, Json(response));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task APrincipalTheServiceBuiltItself_HasNoTokenTenant_AndIsNotJudged()
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await host.CallAsync(HostFixture.Administrator);

        Assert.True(response.Success, Json(response));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ATokenWithoutSubOrClientId_IsTokenInvalid()
    {
        var handler = new ChangePasswordHandler();
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", CkMethodAuthorizationOptions.ApiScope), new Claim("tenant_id", CallTenant),
                new Claim("role", Administrators)], "Bearer"));

        var response = await host.CallAsync(CkMethodPrincipal.FromClaimsPrincipal(anonymous));

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(CkMethodTokenReasons.TokenInvalid, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ABusHandlerTheServiceCallsItself_IsCheckedToo_TheBusHostIsUnchanged()
    {
        // AB#8411: a bus handler of this service, called by the service itself, runs on the in-process transport
        var handler = new ChangePasswordHandler();
        var foreign = CkMethodPrincipal.FromClaimsPrincipal(User(OtherTenant));
        await using var host = await Host.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId),
            busHosted: true, busPrincipal: foreign with { TenantId = CallTenant });

        var local = await host.CallAsync(foreign);
        AssertTenantRefusal(local);
        Assert.Equal(0, handler.Calls);

        // over the bus the host judges the forwarded token itself (ICkMethodCallerTokenValidator); the in-process check is not
        // part of that path: a principal the validator accepted runs exactly as before
        var busResponse = await host.Provider.GetRequiredService<CkMethodHandlerInvoker>()
            .InvokeAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(busResponse.Success, Json(busResponse));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OnTheBusTransport_TheFilterLetsTheCallPass()
    {
        // the bus host re-validates the forwarded token; the calling side does not judge it a second time
        var filter = new CkMethodCallerTenantFilter(Options.Create(new TenantAuthorizationOptions()),
            NullLogger<CkMethodCallerTenantFilter>.Instance);
        var passed = false;
        using (CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(User(OtherTenant))))
        {
            var response = await filter.InvokeAsync(
                new CkMethodInvocationContext(CkMethodTransportNames.Bus, Request(), TestContext.Current.CancellationToken),
                r =>
                {
                    passed = true;
                    return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "null"));
                });
            Assert.True(response.Success);
        }

        Assert.True(passed);
        Assert.Equal(CkMethodInvocationFilterOrder.CallerTenant, filter.Order);
        Assert.True(filter.Order > CkMethodInvocationFilterOrder.Audit && filter.Order < CkMethodInvocationFilterOrder.Authorization);
    }

    // ---------------------------------------------------------------- helpers

    private const string Administrators = "Administrators";

    private static ClaimsPrincipal User(string? tenantId) => Token(
        [new Claim("sub", "0123456789abcdef01234567")], tenantId);

    private static ClaimsPrincipal Service(string clientId, string? tenantId) => Token([new Claim("client_id", clientId)], tenantId);

    private static ClaimsPrincipal Token(IEnumerable<Claim> identity, string? tenantId)
    {
        var claims = identity.Concat([
            new Claim("scope", CkMethodAuthorizationOptions.ApiScope),
            new Claim("role", CkMethodAuthorizationOptions.DefaultAdministratorRoles.First())
        ]).ToList();
        if (tenantId != null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static CkMethodRequest Request() =>
        TestData.Request(DateTime.UtcNow.AddSeconds(30), CallTenant) with { MethodId = TestData.MethodId };

    private static string Json(CkMethodResponse response) => JsonSerializer.Serialize(response, Wire);

    private static void AssertTenantRefusal(CkMethodResponse response)
    {
        Assert.False(response.Success, Json(response));
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    private sealed class RecordingAuditRecorder : ICkMethodAuditRecorder
    {
        public List<CkMethodAuditRecord> Records { get; } = [];

        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            lock (Records)
            {
                Records.Add(record);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     A service with the real registration (<c>AddCkInProcessMethodHandlers</c> or <c>AddCkMethodHandlers</c>), the
    ///     dispatcher and the audit, configured like a local stack: the HTTP tenant gate only logs (both modes LogOnly) and one
    ///     client is on the cross-tenant allow-list.
    /// </summary>
    private sealed class Host : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }

        public required FakeLogCollector Logs { get; init; }

        public required RecordingAuditRecorder Audit { get; init; }

        public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));

        public static async Task<Host> StartAsync(ChangePasswordHandler handler,
            Action<ICkMethodHandlerRegistrationBuilder> configure, bool busHosted = false, CkMethodPrincipal? busPrincipal = null)
        {
            var definition = TestData.Definition(true, "PASSWORD_TOO_SHORT");
            var logs = new FakeLogCollector();
            var audit = new RecordingAuditRecorder();
            var resolver = A.Fake<ICkMethodDeclaringElementResolver>();
            A.CallTo(() => resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TenantAuthorizationOptions.SectionName}:UserTokenEnforcement"] = "LogOnly",
                [$"{TenantAuthorizationOptions.SectionName}:ServiceTokenEnforcement"] = "LogOnly",
                [$"{TenantAuthorizationOptions.SectionName}:CrossTenantServiceClientIds:0"] = AllowedService
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(A.Fake<IDistributionEventHubService>());
            services.AddSingleton(A.Fake<IEventHubControl>());
            services.AddSingleton<ICkMethodDefinitionSource>(new FixedDefinitionSource(definition));
            services.AddSingleton<ICkMethodSensitivityResolver>(new DefinitionSensitivity(definition));
            services.AddSingleton(resolver);
            services.AddSingleton<ICkMethodCallerTokenValidator>(
                new FixedPrincipalValidator(busPrincipal ?? HostFixture.Administrator));
            services.AddSingleton<ICkMethodAuditRecorder>(audit);
            services.AddScoped(_ => handler);

            if (busHosted)
            {
                services.AddCkMethodHandlers("SelfService", configure);
            }
            else
            {
                services.AddCkInProcessMethodHandlers(configure);
            }

            services.AddCkMethodDispatcher();
            services.AddCkMethodAudit();

            var provider = services.BuildServiceProvider();
            // the HTTP gate really is in LogOnly: the in-process check does not depend on it
            var gate = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;
            Assert.Equal(UserTokenTenantEnforcementMode.LogOnly, gate.UserTokenEnforcement);
            Assert.Equal(ServiceTokenTenantEnforcementMode.LogOnly, gate.ServiceTokenEnforcement);

            if (busHosted)
            {
                await ActivatorUtilities.CreateInstance<CkMethodHandlerValidationService>(provider).InitializeAsync();
            }

            await ActivatorUtilities.CreateInstance<CkInProcessMethodValidationService>(provider).InitializeAsync();
            return new Host { Provider = provider, Logs = logs, Audit = audit };
        }

        public async Task<CkMethodResponse> CallAsync(CkMethodPrincipal principal)
        {
            using (CkMethodPrincipalScope.Begin(principal))
            {
                return await Provider.GetRequiredService<ICkMethodDispatcher>()
                    .InvokeAsync(Request(), TestContext.Current.CancellationToken);
            }
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
