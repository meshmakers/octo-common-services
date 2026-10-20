using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization.Token;

public class CkMethodCallerTokenValidatorTests
{
    private static void AssertRefused(CkMethodTokenValidationResult result, string reason)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Principal);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, result.Failure!.Code);
        Assert.Equal(reason, result.Failure.Parameters[CkMethodErrorParameters.Reason]);
    }

    [Fact]
    public async Task ValidUserToken_GivesThePrincipalOfTheToken()
    {
        using var f = new ValidatorFixture();

        var result = await f.Validate(TokenFactory.Create(f.Key));

        Assert.True(result.IsValid);
        var principal = result.Principal!;
        Assert.Equal(TokenFactory.UserSubject, principal.SubjectId);
        Assert.Equal(TokenFactory.UserSubject, principal.Subject);
        Assert.Equal(TokenFactory.UserClient, principal.ClientId);
        Assert.Equal(TokenFactory.Tenant, principal.TenantId);
        Assert.False(principal.IsServiceToken);
        Assert.Equal(["Reporting", "UserManagement"], principal.Roles.Order().ToArray());
        Assert.Contains("octo_api", principal.Scopes);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ScopeAsOneSpaceSeparatedString_IsAccepted()
    {
        using var f = new ValidatorFixture();

        var result = await f.Validate(TokenFactory.Create(f.Key, c => c["scope"] = "openid octo_api"));

        Assert.True(result.IsValid);
        Assert.Equal(["octo_api", "openid"], result.Principal!.Scopes.Order().ToArray());
    }

    [Fact]
    public async Task ServiceTokenWithoutSub_IsAcceptedViaClientId_AndTheSubjectIsTheClient()
    {
        using var f = new ValidatorFixture();

        var result = await f.Validate(TokenFactory.Create(f.Key, user: false));

        Assert.True(result.IsValid);
        Assert.Null(result.Principal!.SubjectId);
        Assert.Equal(TokenFactory.ServiceClient, result.Principal.Subject);
        Assert.True(result.Principal.IsServiceToken);
        Assert.Empty(result.Principal.Roles);
    }

    [Fact]
    public async Task ServiceTokenWithoutClientId_IsRefused()
    {
        using var f = new ValidatorFixture();

        var result = await f.Validate(TokenFactory.Create(f.Key, c => c.Remove("client_id"), user: false));

        AssertRefused(result, CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task ServiceTokenOfAnotherTenant_IsRefused_UnlessTheClientIsInTheCrossTenantList()
    {
        using var f = new ValidatorFixture();
        var token = TokenFactory.Create(f.Key, user: false, tenant: "othertenant");

        AssertRefused(await f.Validate(token), CkMethodTokenReasons.TenantNotAllowed);

        using var listed = new ValidatorFixture(tenantOptions: new TenantAuthorizationOptions
        {
            CrossTenantServiceClientIds = [TokenFactory.ServiceClient]
        });
        var allowed = await listed.Validate(TokenFactory.Create(listed.Key, user: false, tenant: "othertenant"));
        Assert.True(allowed.IsValid);
        Assert.Equal(TokenFactory.Tenant, allowed.Principal!.TenantId); // validated for the tenant of the request
    }

    [Theory]
    [InlineData(ServiceTokenTenantEnforcementMode.LogOnly)]
    [InlineData(ServiceTokenTenantEnforcementMode.Disabled)]
    public async Task ServiceTokenWithoutTenant_IsRefused_InEveryModeOfTheHttpSide(ServiceTokenTenantEnforcementMode mode)
    {
        using var f = new ValidatorFixture(tenantOptions: new TenantAuthorizationOptions { ServiceTokenEnforcement = mode });

        var result = await f.Validate(TokenFactory.Create(f.Key, user: false, tenant: null));

        AssertRefused(result, CkMethodTokenReasons.TenantNotAllowed);
    }

    [Theory]
    [InlineData(UserTokenTenantEnforcementMode.LogOnly)]
    [InlineData(UserTokenTenantEnforcementMode.Enforce)]
    public async Task UserTokenOfAnotherTenant_IsRefused_InEveryModeOfTheHttpSide(UserTokenTenantEnforcementMode mode)
    {
        using var f = new ValidatorFixture(tenantOptions: new TenantAuthorizationOptions { UserTokenEnforcement = mode });

        var result = await f.Validate(TokenFactory.Create(f.Key, tenant: "othertenant"));

        AssertRefused(result, CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task TokenOfAParentTenant_IsRefused_ThereIsNoParentTenantRule()
    {
        using var f = new ValidatorFixture();

        // the token was issued for the parent; the request addresses its child
        var result = await f.Validate(TokenFactory.Create(f.Key, tenant: "parenttenant"), "parenttenant-child");

        AssertRefused(result, CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task UserTokenWithoutTenantClaim_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, tenant: null)), CkMethodTokenReasons.TenantNotAllowed);
    }

    [Fact]
    public async Task AllowedTenants_IsASelectionHint_NeverAnAuthorization()
    {
        using var f = new ValidatorFixture();

        // allowed_tenants lists the request tenant, tenant_id names another one: refused
        var hinted = TokenFactory.Create(f.Key, c => c["allowed_tenants"] = new[] { TokenFactory.Tenant }, tenant: "othertenant");
        AssertRefused(await f.Validate(hinted), CkMethodTokenReasons.TenantNotAllowed);

        // no allowed_tenants at all, tenant_id matches: accepted
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
    }

    [Fact]
    public async Task TenantComparison_IgnoresCase_LikeTheHttpMiddleware()
    {
        using var f = new ValidatorFixture();

        Assert.True((await f.Validate(TokenFactory.Create(f.Key, tenant: "MeshTest"))).IsValid);
    }

    [Fact]
    public async Task TokenWithoutOctoApiScope_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, scope: false)), CkMethodTokenReasons.TokenInvalid);
        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, c => c["scope"] = new[] { "openid", "octo_api2" })),
            CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task ExpiredToken_GivesTokenExpired_AndATokenInsideTheSkewPasses()
    {
        using var f = new ValidatorFixture();

        var expired = await f.Validate(TokenFactory.Create(f.Key, expires: DateTime.UtcNow.AddMinutes(-2)));
        var insideSkew = await f.Validate(TokenFactory.Create(f.Key, expires: DateTime.UtcNow.AddSeconds(-10)));
        var beyondSkew = await f.Validate(TokenFactory.Create(f.Key, expires: DateTime.UtcNow.AddSeconds(-60)));

        AssertRefused(expired, CkMethodTokenReasons.TokenExpired);
        Assert.True(insideSkew.IsValid);
        AssertRefused(beyondSkew, CkMethodTokenReasons.TokenExpired);
    }

    [Fact]
    public async Task ExpiredTokenOfAnotherTenant_ReportsTheTenant_NotTheExpiry()
    {
        using var f = new ValidatorFixture();

        var result = await f.Validate(TokenFactory.Create(f.Key, tenant: "othertenant", expires: DateTime.UtcNow.AddMinutes(-5)));

        AssertRefused(result, CkMethodTokenReasons.TenantNotAllowed); // a fresh token would not help
    }

    [Fact]
    public async Task ExpiredTokenWithAForgedSignature_IsInvalid_NotExpired()
    {
        using var f = new ValidatorFixture();
        using var foreign = new TestKey(f.Key.Kid); // same kid, other key

        var result = await f.Validate(TokenFactory.Create(foreign, expires: DateTime.UtcNow.AddMinutes(-5)));

        AssertRefused(result, CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task WrongIssuer_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, issuer: "https://evil.test/")), CkMethodTokenReasons.TokenInvalid);
        // the issuer carries the trailing slash of the identity service; the form without it is another issuer
        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, issuer: "https://identity.test")), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task WrongSignature_IsRefused_AlsoWithTheKeyIdOfAKnownKey()
    {
        using var f = new ValidatorFixture();
        using var foreign = new TestKey("key-1");
        using var foreignOtherKid = new TestKey("unknown-kid");

        AssertRefused(await f.Validate(TokenFactory.Create(foreign)), CkMethodTokenReasons.TokenInvalid);
        AssertRefused(await f.Validate(TokenFactory.Create(foreignOtherKid)), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task AlgNone_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(TokenFactory.Unsigned()), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task SymmetricTokenSignedWithThePublicKey_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(TokenFactory.SymmetricWithPublicKey(f.Key)), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task MissingOrMalformedToken_IsRefused()
    {
        using var f = new ValidatorFixture();

        AssertRefused(await f.Validate(null), CkMethodTokenReasons.TokenMissing);
        AssertRefused(await f.Validate(""), CkMethodTokenReasons.TokenMissing);
        AssertRefused(await f.Validate("   "), CkMethodTokenReasons.TokenMissing);
        AssertRefused(await f.Validate("not-a-jwt"), CkMethodTokenReasons.TokenInvalid);
        AssertRefused(await f.Validate("a.b.c"), CkMethodTokenReasons.TokenInvalid);
        AssertRefused(await f.Validate(new string('x', 40_000)), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task AudienceNotConfigured_IsNotValidated()
    {
        using var f = new ValidatorFixture();

        // documents the platform default: with no audience configured every octo_api token of the tenant is accepted
        Assert.True((await f.Validate(TokenFactory.Create(f.Key, audience: "some-other-api"))).IsValid);
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
    }

    [Fact]
    public async Task AudienceConfigured_AcceptsAMatchingAudience_AndRefusesAnotherOne()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { ValidAudiences = ["octo-methods", "octo-api"] });

        Assert.True((await f.Validate(TokenFactory.Create(f.Key, audience: "octo-api"))).IsValid);
        AssertRefused(await f.Validate(TokenFactory.Create(f.Key, audience: "some-other-api")),
            CkMethodTokenReasons.AudienceMismatch);
        AssertRefused(await f.Validate(TokenFactory.Create(f.Key)), CkMethodTokenReasons.AudienceMismatch);
    }

    [Fact]
    public async Task AudienceMismatchWithAForgedSignature_IsInvalid()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { ValidAudiences = ["octo-api"] });
        using var foreign = new TestKey(f.Key.Kid);

        AssertRefused(await f.Validate(TokenFactory.Create(foreign, audience: "x")), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task ConfiguredIssuer_ReplacesTheAuthorityIssuer()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { ValidIssuer = "https://issuer.test/" });

        Assert.True((await f.Validate(TokenFactory.Create(f.Key, issuer: "https://issuer.test/"))).IsValid);
        AssertRefused(await f.Validate(TokenFactory.Create(f.Key)), CkMethodTokenReasons.TokenInvalid);
    }

    [Fact]
    public async Task ValidTypes_WhenConfigured_AreEnforced()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { ValidTypes = ["at+jwt"] });

        AssertRefused(await f.Validate(TokenFactory.Create(f.Key)), CkMethodTokenReasons.TokenInvalid); // typ JWT
    }

    [Fact]
    public async Task Keys_AreCached_AcrossCalls()
    {
        using var f = new ValidatorFixture();

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
        }

        Assert.Equal(2, f.Identity.Requests); // discovery document + JWKS, once
    }

    [Fact]
    public async Task KeyRotation_IsFollowed_WithOneForcedRefresh()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { KeyRefreshInterval = TimeSpan.FromSeconds(1) });
        using var rotated = new TestKey("key-2");
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);

        f.Identity.Publish(f.Key, rotated);
        f.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.True((await f.Validate(TokenFactory.Create(rotated))).IsValid);
        Assert.Equal(4, f.Identity.Requests); // 2 for the first download, 2 for the forced refresh
    }

    [Fact]
    public async Task KeyRotation_WithinTheRefreshInterval_IsNotFollowed()
    {
        using var f = new ValidatorFixture(); // default 5 minutes
        using var rotated = new TestKey("key-2");
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);

        f.Identity.Publish(f.Key, rotated);
        f.Time.Advance(TimeSpan.FromMinutes(1));

        AssertRefused(await f.Validate(TokenFactory.Create(rotated)), CkMethodTokenReasons.TokenInvalid);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await f.Validate(TokenFactory.Create(rotated))).IsValid);
    }

    [Fact]
    public async Task CachedKeys_StayInUse_WhileTheIdentityServiceIsDown()
    {
        using var f = new ValidatorFixture();
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);

        f.Identity.Unavailable = true;
        f.Time.Advance(TimeSpan.FromHours(13)); // the scheduled refresh falls due

        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
        var requests = f.Identity.Requests;
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
        Assert.Equal(requests, f.Identity.Requests); // not retried on every call
    }

    [Fact]
    public async Task UnknownKeyIds_DoNotHammerTheIdentityService()
    {
        using var f = new ValidatorFixture();
        using var unknown = new TestKey("never-published");

        for (var i = 0; i < 10; i++)
        {
            AssertRefused(await f.Validate(TokenFactory.Create(unknown)), CkMethodTokenReasons.TokenInvalid);
        }

        Assert.True(f.Identity.Requests <= 4, $"identity requests: {f.Identity.Requests}");
    }

    [Fact]
    public async Task IdentityUnreachable_FailsClosed_ByThrowing_NotByRefusingTheCaller()
    {
        using var f = new ValidatorFixture();
        f.Identity.Unavailable = true;

        await Assert.ThrowsAsync<CkMethodTokenKeysUnavailableException>(async () =>
            await f.Validate(TokenFactory.Create(f.Key)));

        f.Identity.Unavailable = false; // recovers on its own
        Assert.True((await f.Validate(TokenFactory.Create(f.Key))).IsValid);
    }

    [Fact]
    public async Task NoAuthorityConfigured_ThrowsInsteadOfTrustingTheToken()
    {
        var logs = new Microsoft.Extensions.Logging.Testing.FakeLogCollector();
        var validator = new CkMethodCallerTokenValidator(
            Microsoft.Extensions.Options.Options.Create(new CkMethodTokenOptions()),
            Microsoft.Extensions.Options.Options.Create(new TenantAuthorizationOptions()),
            new FakeHttpClientFactory(new FakeIdentityHandler()),
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodCallerTokenValidator>(logs));
        using var key = new TestKey("k");

        await Assert.ThrowsAsync<CkMethodTokenKeysUnavailableException>(async () =>
            await validator.ValidateAsync(TokenFactory.Create(key), "meshtest", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisabledValidation_SkipsSignatureAndExpiry_ButKeepsTheTenantAndScopeRules()
    {
        using var f = new ValidatorFixture(new CkMethodTokenOptions { DisableValidation = true });
        using var foreign = new TestKey("foreign");

        var accepted = await f.Validate(TokenFactory.Create(foreign, expires: DateTime.UtcNow.AddMinutes(-60)));

        Assert.True(accepted.IsValid);
        AssertRefused(await f.Validate(TokenFactory.Create(foreign, tenant: "othertenant")),
            CkMethodTokenReasons.TenantNotAllowed);
        AssertRefused(await f.Validate("garbage"), CkMethodTokenReasons.TokenInvalid);
        Assert.Equal(0, f.Identity.Requests);
    }

    [Fact]
    public async Task NoLogLine_ContainsTheTokenAClaimValueOrKeyMaterial()
    {
        using var f = new ValidatorFixture();
        var valid = TokenFactory.Create(f.Key);
        var expired = TokenFactory.Create(f.Key, expires: DateTime.UtcNow.AddMinutes(-5));
        var foreign = TokenFactory.Create(new TestKey("x"));
        var wrongTenant = TokenFactory.Create(f.Key, tenant: "secret-other-tenant");
        var service = TokenFactory.Create(f.Key, user: false, tenant: "service-tenant-x");

        foreach (var token in new[] { valid, expired, foreign, wrongTenant, service })
        {
            await f.Validate(token);
        }

        f.Identity.Unavailable = false;
        var log = f.AllLogText();
        Assert.NotEmpty(f.Logs.GetSnapshot()); // the refusals are logged ...
        foreach (var secret in new[]
                 {
                     valid, expired, foreign, wrongTenant, service, TokenFactory.UserSubject, TokenFactory.UserClient,
                     TokenFactory.ServiceClient, "secret-other-tenant", "service-tenant-x", "UserManagement",
                     Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(f.Key.Rsa.ExportParameters(false).Modulus)
                 })
        {
            Assert.DoesNotContain(secret, log); // ... without anything taken from the token
        }
    }

    [Fact]
    public async Task ExtensionOverload_UsesTheTokenAndTheTenantOfTheRequest()
    {
        using var f = new ValidatorFixture();
        var request = new CkMethodRequest(TokenFactory.Tenant)
        {
            InvocationId = Guid.NewGuid(), MethodId = "A/B.C-1", CkTypeId = "A/B", DeadlineUtc = DateTime.UtcNow.AddSeconds(15),
            CallerAccessToken = TokenFactory.Create(f.Key),
            Caller = new CkMethodCaller { SubjectId = "administrator" }
        };

        var result = await ((ICkMethodCallerTokenValidator)f.Validator).ValidateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(TokenFactory.UserSubject, result.Principal!.SubjectId); // never the forged Caller member
    }
}
