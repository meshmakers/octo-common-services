using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization.Token;

/// <summary>The remote handler host with token validation switched on (opt-in), end to end with a fake identity service.</summary>
public class RemoteHandlerTokenValidationTests
{
    private static (HostFixture Host, ChangePasswordHandler Handler, ValidatorFixture Token) Build(
        bool validate = true, bool allowAnyCaller = false)
    {
        var token = new ValidatorFixture();
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.DefinitionWithRoles("UserManagement"),
            tokenValidator: validate ? token.Validator : null, withoutValidator: !validate,
            evaluator: allowAnyCaller ? new AllowAnyCallerEvaluator() : null);
        return (host, handler, token);
    }

    private static CkMethodRequest Request(HostFixture host, string? token, CkMethodCaller? caller = null) =>
        TestData.Request(host.InSeconds(15)) with
        {
            CallerAccessToken = token,
            Caller = caller ?? new CkMethodCaller { SubjectId = "administrator", Roles = ["AdminPanelManagement"] }
        };

    private static void AssertForbidden(CkMethodResponse response, string reason, ChangePasswordHandler handler)
    {
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(reason, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ValidToken_RunsTheHandler_WithThePrincipalOfTheToken()
    {
        var (host, handler, token) = Build();
        using var _ = token;

        var response = await host.Invoker.InvokeAsync(Request(host, TokenFactory.Create(token.Key)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        var principal = handler.LastContext!.CallerPrincipal!;
        Assert.Equal(TokenFactory.UserSubject, principal.Subject);
        Assert.Contains("UserManagement", principal.Roles);
        Assert.Equal("meshtest", principal.TenantId);
        // the informational member is untouched and not the source of the principal
        Assert.Equal("administrator", handler.LastContext.Caller!.SubjectId);
    }

    [Fact]
    public async Task ServiceToken_RunsTheHandler_WithTheClientAsSubject()
    {
        var (host, handler, token) = Build(allowAnyCaller: true);
        using var _ = token;

        var response = await host.Invoker.InvokeAsync(Request(host, TokenFactory.Create(token.Key, user: false)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(TokenFactory.ServiceClient, handler.LastContext!.CallerPrincipal!.Subject);
        Assert.True(handler.LastContext.CallerPrincipal.IsServiceToken);
    }

    [Fact]
    public async Task ForgedCallerWithoutToken_IsForbidden_AndTheHandlerIsNotCalled()
    {
        var (host, handler, token) = Build();
        using var _ = token;

        var response = await host.Invoker.InvokeAsync(Request(host, null), TestContext.Current.CancellationToken);

        AssertForbidden(response, CkMethodTokenReasons.TokenMissing, handler);
    }

    [Fact]
    public async Task EveryRefusedToken_IsForbidden_NotHandlerFailed_AndTheHandlerIsNotCalled()
    {
        var (host, handler, token) = Build();
        using var _ = token;
        using var foreign = new TestKey(token.Key.Kid);
        var cases = new (string Name, string Token, string Reason)[]
        {
            ("other tenant", TokenFactory.Create(token.Key, tenant: "othertenant"), CkMethodTokenReasons.TenantNotAllowed),
            ("parent tenant", TokenFactory.Create(token.Key, tenant: "meshtest-parent"), CkMethodTokenReasons.TenantNotAllowed),
            ("wrong issuer", TokenFactory.Create(token.Key, issuer: "https://evil.test/"), CkMethodTokenReasons.TokenInvalid),
            ("wrong signature", TokenFactory.Create(foreign), CkMethodTokenReasons.TokenInvalid),
            ("alg none", TokenFactory.Unsigned(), CkMethodTokenReasons.TokenInvalid),
            ("no octo_api", TokenFactory.Create(token.Key, scope: false), CkMethodTokenReasons.TokenInvalid),
            ("expired", TokenFactory.Create(token.Key, expires: DateTime.UtcNow.AddMinutes(-5)), CkMethodTokenReasons.TokenExpired),
            ("service without client", TokenFactory.Create(token.Key, c => c.Remove("client_id"), user: false), CkMethodTokenReasons.TokenInvalid),
            ("service other tenant", TokenFactory.Create(token.Key, user: false, tenant: "othertenant"), CkMethodTokenReasons.TenantNotAllowed)
        };

        foreach (var (name, jwt, reason) in cases)
        {
            var response = await host.Invoker.InvokeAsync(Request(host, jwt), TestContext.Current.CancellationToken);
            Assert.True(!response.Success, name);
            Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
            Assert.True(reason == response.Error.Parameters[CkMethodErrorParameters.Reason], name);
        }

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ExpiredToken_IsDistinguishable_AndATokenInsideTheSkewRuns()
    {
        var (host, handler, token) = Build();
        using var _ = token;

        var expired = await host.Invoker.InvokeAsync(
            Request(host, TokenFactory.Create(token.Key, expires: DateTime.UtcNow.AddMinutes(-2))),
            TestContext.Current.CancellationToken);
        AssertForbidden(expired, CkMethodTokenReasons.TokenExpired, handler);

        var inSkew = await host.Invoker.InvokeAsync(
            Request(host, TokenFactory.Create(token.Key, expires: DateTime.UtcNow.AddSeconds(-10))),
            TestContext.Current.CancellationToken);
        Assert.True(inSkew.Success);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TokenIsChecked_BeforeTheParametersAreInterpreted()
    {
        var (host, handler, token) = Build();
        using var _ = token;
        var request = Request(host, null) with { ParametersJson = "{ not json" };

        var response = await host.Invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

        AssertForbidden(response, CkMethodTokenReasons.TokenMissing, handler); // not METHOD_VALIDATION_FAILED
    }

    [Fact]
    public async Task BusConsumer_AppliesTheSameValidation()
    {
        var (host, handler, token) = Build();
        using var _ = token;

        var refused = await host.BusConsumer.HandleAsync(Request(host, "garbage"));
        AssertForbidden(refused, CkMethodTokenReasons.TokenInvalid, handler);

        var accepted = await host.BusConsumer.HandleAsync(Request(host, TokenFactory.Create(token.Key)));
        Assert.True(accepted.Success);
    }

    [Fact]
    public async Task IdentityUnreachable_IsHandlerFailed_NotForbidden_AndTheHandlerIsNotCalled()
    {
        var (host, handler, token) = Build();
        using var _ = token;
        token.Identity.Unavailable = true;

        var response = await host.Invoker.InvokeAsync(Request(host, TokenFactory.Create(token.Key)),
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WithoutOptIn_NoTokenIsValidated_AndTheCallerIsRefused()
    {
        var (host, handler, token) = Build(validate: false);
        using var _ = token;

        var response = await host.Invoker.InvokeAsync(Request(host, null), TestContext.Current.CancellationToken);

        // no validated caller, so the declared authorization cannot be met (AB#7664): refused, handler never runs
        AssertForbidden(response, CkMethodAuthorizationErrors.ReasonNotAuthorized, handler);
        Assert.Equal(0, token.Identity.Requests);
    }

    [Fact]
    public async Task NoToken_AndNoClaimValue_ReachesTheHostLog_OnSuccessOrFailure()
    {
        var (host, _, token) = Build();
        using var _ = token;
        var good = TokenFactory.Create(token.Key);
        var expired = TokenFactory.Create(token.Key, expires: DateTime.UtcNow.AddMinutes(-5));
        var wrongTenant = TokenFactory.Create(token.Key, tenant: "secret-other-tenant");

        foreach (var jwt in new[] { good, expired, wrongTenant, "garbage-token-value" })
        {
            await host.Invoker.InvokeAsync(Request(host, jwt), TestContext.Current.CancellationToken);
        }

        var log = host.AllLogText() + token.AllLogText();
        Assert.NotEmpty(host.Logs.GetSnapshot());
        foreach (var secret in new[]
                 {
                     good, expired, wrongTenant, "garbage-token-value", TokenFactory.UserSubject, TokenFactory.UserClient,
                     "secret-other-tenant", "administrator"
                 })
        {
            Assert.DoesNotContain(secret, log);
        }
    }

    // ---- in-process transport --------------------------------------------------------------------------------

    private sealed class SpyValidator : ICkMethodCallerTokenValidator
    {
        public int Calls { get; private set; }

        public ValueTask<CkMethodTokenValidationResult> ValidateAsync(string? accessToken, string expectedTenantId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("the in-process transport must not validate a token");
        }
    }

    [Fact]
    public async Task InProcess_DoesNotInvokeTheValidator_AndPassesTheGatewayPrincipalThrough()
    {
        var spy = new SpyValidator();
        var handler = new ChangePasswordHandler();
        var services = new ServiceCollection();
        services.AddScoped(_ => handler);
        services.AddSingleton<ICkMethodCallerTokenValidator>(spy);
        var inProcess = InProcessFixture.Build<ChangePasswordHandler>(services, TestData.DefinitionWithRoles("UserManagement"));
        var gateway = new CkMethodPrincipal("gateway-user", ["UserManagement"], ["octo_api"]);
        var request = TestData.Request(inProcess.InSeconds(15)) with { CallerAccessToken = null };

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(gateway))
        {
            response = await inProcess.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
        }

        Assert.True(response.Success);
        Assert.Equal(0, spy.Calls);
        Assert.Same(gateway, handler.LastContext!.CallerPrincipal);
    }

    [Fact]
    public async Task InProcess_WithoutAGatewayPrincipal_IsRefused()
    {
        var handler = new ChangePasswordHandler();
        var services = new ServiceCollection();
        services.AddScoped(_ => handler);
        services.AddSingleton<ICkMethodPrincipalProvider>(new NoPrincipalProvider());
        var inProcess = InProcessFixture.Build<ChangePasswordHandler>(services, TestData.Definition());

        var response = await inProcess.Transport.InvokeAsync(TestData.Request(inProcess.InSeconds(15)),
            TestContext.Current.CancellationToken);

        AssertForbidden(response, CkMethodAuthorizationErrors.ReasonNotAuthorized, handler);
    }

    // ---- registration --------------------------------------------------------------------------------------------

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ServiceProvider Register(string environment, Action<CkMethodTokenOptions> configure,
        FakeIdentityHandler? identity = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddCkMethodCallerTokenValidation(configure);
        if (identity != null)
        {
            services.AddHttpClient(CkMethodTokenOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => identity);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Registration_ResolvesTheValidator_AndMirrorsTheHttpApiDefaults()
    {
        using var key = new TestKey("key-1");
        var identity = new FakeIdentityHandler(key);
        await using var provider = Register("Production", o => o.Authority = "https://identity.test");

        var options = provider.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value;
        Assert.Equal("octo_api", options.RequiredScope);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ClockSkew);
        Assert.Empty(options.ValidAudiences);
        Assert.True(options.RequireHttpsMetadata);
        Assert.NotNull(provider.GetRequiredService<ICkMethodCallerTokenValidator>());
        GC.KeepAlive(identity);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Registration_ValidatesATokenAgainstTheJwksOfTheAuthority()
    {
        using var key = new TestKey("key-1");
        var identity = new FakeIdentityHandler(key);
        await using var provider = Register("Production", o => o.Authority = "https://identity.test", identity);

        var validator = provider.GetRequiredService<ICkMethodCallerTokenValidator>();
        var result = await validator.ValidateAsync(TokenFactory.Create(key), "meshtest", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Startup_IsRefused_WhenValidationIsDisabledOutsideDevelopment()
    {
        await using var production = Register("Production", o => o.DisableValidation = true);
        await using var staging = Register("Staging", o => o.DisableValidation = true);
        await using var development = Register("Development", o => o.DisableValidation = true);

        Assert.Throws<OptionsValidationException>(() => production.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => staging.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value);
        Assert.True(development.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value.DisableValidation);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Startup_IsRefused_WithoutAnAuthority_OrWithAnAbsurdSkew()
    {
        await using var noAuthority = Register("Production", _ => { });
        await using var skew = Register("Production", o => { o.Authority = "https://identity.test"; o.ClockSkew = TimeSpan.FromHours(1); });

        Assert.Throws<OptionsValidationException>(() => noAuthority.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => skew.GetRequiredService<IOptions<CkMethodTokenOptions>>().Value);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Registration_ReadsTheCrossTenantServiceClientsOfTheHttpSide()
    {
        using var key = new TestKey("key-1");
        var identity = new FakeIdentityHandler(key);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment("Production"));
        services.Configure<TenantAuthorizationOptions>(o => o.CrossTenantServiceClientIds = [TokenFactory.ServiceClient]);
        services.AddCkMethodCallerTokenValidation(o => o.Authority = "https://identity.test");
        services.AddHttpClient(CkMethodTokenOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => identity);
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<ICkMethodCallerTokenValidator>().ValidateAsync(
            TokenFactory.Create(key, user: false, tenant: "othertenant"), "meshtest", TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
    }
}

/// <summary>For tests of the principal's propagation only: the authorization rules are covered elsewhere.</summary>
internal sealed class AllowAnyCallerEvaluator : ICkMethodAuthorizationEvaluator
{
    public CkMethodAuthorizationDecision Evaluate(Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkMethodAuthorizationDto? declaration,
        Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkMethodKindDto kind, string? targetRtId,
        CkMethodPrincipal? principal) => CkMethodAuthorizationDecision.Allow;
}
