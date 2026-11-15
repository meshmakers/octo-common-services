using System.Security.Claims;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Tests.Authorization.Token;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     AB#8999: the tenant generation step of the caller-tenant rule, on the in-process transport
///     (<see cref="CkMethodCallerTenantFilter" />) and on the bus host (<see cref="CkMethodCallerTokenValidator" />), in
///     LogOnly and Enforce: match, mismatch, missing, not checked, lookup failure, cross-tenant client.
/// </summary>
public class CkMethodCallerTenantGenerationTests
{
    private const string CallTenant = "meshtest";
    private const string Current = "1794750381540";
    private const string Old = "1794750081540";
    private const string CrossClient = "platform-worker";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Case
    {
        Match,
        Mismatch,
        Missing,
        NotChecked,
        LookupFailed,
        CrossTenantClient
    }

    // expected: (allowed in LogOnly, allowed in Enforce, Enforce error code when refused)
    public static TheoryData<Case, TenantGenerationEnforcementMode, string?> Matrix()
    {
        var data = new TheoryData<Case, TenantGenerationEnforcementMode, string?>();
        foreach (var mode in new[] { TenantGenerationEnforcementMode.LogOnly, TenantGenerationEnforcementMode.Enforce })
        {
            foreach (var c in Enum.GetValues<Case>())
            {
                string? expectedError = null;
                if (mode == TenantGenerationEnforcementMode.Enforce)
                {
                    expectedError = c switch
                    {
                        Case.Mismatch or Case.Missing => CkMethodErrorCodes.MethodForbidden,
                        Case.LookupFailed => CkMethodErrorCodes.MethodHandlerFailed,
                        _ => null
                    };
                }

                data.Add(c, mode, expectedError);
            }
        }

        return data;
    }

    // ---------------------------------------------------------------- in-process transport

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task InProcess(Case c, TenantGenerationEnforcementMode mode, string? expectedError)
    {
        var (check, reader, options) = Build(c, mode);
        var filter = new CkMethodCallerTenantFilter(Options.Create(options),
            NullLogger<CkMethodCallerTenantFilter>.Instance, generationCheck: check);
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Principal(c));
        var passed = false;

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(principal))
        {
            response = await filter.InvokeAsync(
                new CkMethodInvocationContext(CkMethodTransportNames.InProcess, Request(), Ct),
                r =>
                {
                    passed = true;
                    return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "null"));
                });
        }

        if (expectedError is null)
        {
            Assert.True(response.Success);
            Assert.True(passed);
        }
        else
        {
            Assert.False(response.Success);
            Assert.False(passed);
            Assert.Equal(expectedError, response.Error!.Code);
            if (expectedError == CkMethodErrorCodes.MethodForbidden)
            {
                Assert.Equal(CkMethodTokenReasons.TenantNotAllowed,
                    response.Error.Parameters[CkMethodErrorParameters.Reason]);
            }
        }

        // the cross-tenant client's token tenant is never looked up
        if (c == Case.CrossTenantClient)
        {
            Assert.Equal(0, reader.Reads);
        }
    }

    // ---------------------------------------------------------------- bus host

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task BusHost(Case c, TenantGenerationEnforcementMode mode, string? expectedError)
    {
        var (check, reader, options) = Build(c, mode);
        using var f = new ValidatorFixture(tenantOptions: options, generationCheck: check);
        var token = TokenFactory.Create(f.Key, claims =>
        {
            if (Generation(c) is { } generation)
            {
                claims[TenantGenerationClaim.ClaimType] = generation;
            }

            if (c == Case.CrossTenantClient)
            {
                claims["client_id"] = CrossClient;
            }
        }, user: c != Case.CrossTenantClient, tenant: c == Case.CrossTenantClient ? "othertenant" : CallTenant);

        if (expectedError == CkMethodErrorCodes.MethodHandlerFailed)
        {
            await Assert.ThrowsAsync<CkMethodTenantGenerationUnavailableException>(
                async () => await f.Validate(token, CallTenant));
            return;
        }

        var result = await f.Validate(token, CallTenant);
        if (expectedError is null)
        {
            Assert.True(result.IsValid);
            Assert.Equal(Generation(c) is { } g ? TenantGenerationClaim.FromValue(g) : null,
                result.Principal!.TokenTenantGeneration);
        }
        else
        {
            Assert.False(result.IsValid);
            Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, result.Reason);
            // the log carries a fixed category, never the claim value
            Assert.DoesNotContain(Old, f.AllLogText());
            Assert.DoesNotContain(Current, f.AllLogText());
        }

        if (c == Case.CrossTenantClient)
        {
            Assert.Equal(0, reader.Reads);
        }
    }

    [Fact]
    public void FromClaimsPrincipal_CarriesTenantGenerationAndTheRawClientId()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Principal(Case.Match));
        var withClient = CkMethodPrincipal.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            Principal(Case.Match).Claims.Append(new Claim("client_id", "studio")), "Bearer")));

        Assert.Equal(TenantGenerationClaim.FromValue(Current), principal.TokenTenantGeneration);
        Assert.Null(withClient.ClientId); // a user token keeps ClientId null (AB#8078)
        Assert.Equal("studio", withClient.TokenClientId);
        Assert.DoesNotContain(Current, principal.ToString());
    }

    [Fact]
    public async Task BusHost_JsonNumberClaim_IsMismatch_InEnforce()
    {
        var (check, _, options) = Build(Case.Match, TenantGenerationEnforcementMode.Enforce);
        using var f = new ValidatorFixture(tenantOptions: options, generationCheck: check);
        var token = TokenFactory.Create(f.Key, claims => claims[TenantGenerationClaim.ClaimType] = 1794750381540L);

        var result = await f.Validate(token, CallTenant);

        Assert.False(result.IsValid);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, result.Reason);
    }

    // ---------------------------------------------------------------- helpers

    private static (ITenantGenerationCheck Check, StubReader Reader, TenantAuthorizationOptions Options) Build(Case c,
        TenantGenerationEnforcementMode mode)
    {
        var options = new TenantAuthorizationOptions { TenantGenerationEnforcement = mode };
        options.CrossTenantServiceClientIds.Add(CrossClient);
        var reader = new StubReader(c switch
        {
            Case.NotChecked => null,
            _ => Current
        }, c == Case.LookupFailed);
        var check = new TenantGenerationCheck(reader, new TenantGenerationMetrics(), Options.Create(options),
            Options.Create(new OctoSystemConfiguration { SystemTenantId = "OctoSystem" }),
            NullLogger<TenantGenerationCheck>.Instance);
        return (check, reader, options);
    }

    private static string? Generation(Case c) => c switch
    {
        Case.Mismatch => Old,
        Case.Missing => null,
        Case.CrossTenantClient => Old,
        _ => Current
    };

    private static ClaimsPrincipal Principal(Case c)
    {
        var claims = new List<Claim>
        {
            new("scope", CkMethodAuthorizationOptions.ApiScope),
            new("role", CkMethodAuthorizationOptions.DefaultAdministratorRoles.First())
        };
        if (c == Case.CrossTenantClient)
        {
            claims.Add(new Claim("client_id", CrossClient));
            claims.Add(new Claim("tenant_id", "othertenant"));
        }
        else
        {
            claims.Add(new Claim("sub", "0123456789abcdef01234567"));
            claims.Add(new Claim("tenant_id", CallTenant));
        }

        if (Generation(c) is { } generation)
        {
            claims.Add(new Claim(TenantGenerationClaim.ClaimType, generation));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static CkMethodRequest Request() =>
        TestData.Request(DateTime.UtcNow.AddSeconds(30), CallTenant) with { MethodId = TestData.MethodId };

    private sealed class StubReader(string? generation, bool fail) : ITenantGenerationReader
    {
        private int _reads;

        public int Reads => _reads;

        public ValueTask<string?> GetGenerationAsync(string tenantId, bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            if (fail)
            {
                throw new TenantGenerationLookupException(tenantId, new TimeoutException());
            }

            return ValueTask.FromResult(generation);
        }

        public void Invalidate(string tenantId)
        {
        }
    }
}
