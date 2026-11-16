using System.Security.Claims;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     AB#8776: the public caller-tenant check that a host runs before its own checks (the asset-repo method gateway): the same
///     verdicts as the in-process filter of AB#8711, as a refusal with the method id and the reason.
/// </summary>
public class CkMethodCallerTenantCheckTests
{
    private const string CallTenant = "tenant-a";
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";

    private static CkMethodPrincipal Token(params (string Type, string Value)[] claims) =>
        CkMethodPrincipal.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)).Append(new Claim("scope", "octo_api")), "Bearer")));

    private static TenantAuthorizationOptions Options()
    {
        var options = new TenantAuthorizationOptions();
        options.CrossTenantServiceClientIds.Add("allowed-service");
        return options;
    }

    public static TheoryData<string, CkMethodPrincipal?, string?> Cases() => new()
    {
        { "user of the call tenant", Token(("sub", "u1"), ("tenant_id", CallTenant)), null },
        { "user, tenant id in other case", Token(("sub", "u1"), ("tenant_id", "TENANT-A")), null },
        { "user of another tenant", Token(("sub", "u1"), ("tenant_id", "tenant-b")), CkMethodTokenReasons.TenantNotAllowed },
        { "user without tenant_id", Token(("sub", "u1")), CkMethodTokenReasons.TenantNotAllowed },
        { "service of the call tenant", Token(("client_id", "svc"), ("tenant_id", CallTenant)), null },
        { "service of another tenant", Token(("client_id", "svc"), ("tenant_id", "tenant-b")), CkMethodTokenReasons.TenantNotAllowed },
        { "allow-listed service of another tenant", Token(("client_id", "allowed-service"), ("tenant_id", "tenant-b")), null },
        { "token without sub and client_id", Token(("tenant_id", CallTenant)), CkMethodTokenReasons.TokenInvalid },
        { "principal built by a service", new CkMethodPrincipal("job", [], ["octo_api"]), null },
        { "no principal", null, null }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsRefused_FollowsTheCallerTenantRule(string because, CkMethodPrincipal? principal, string? expectedReason)
    {
        var refused = CkMethodCallerTenantCheck.IsRefused(principal, CallTenant, MethodId, Options(), out var error,
            out var verdict);

        Assert.True(refused == (expectedReason != null), because);
        if (expectedReason == null)
        {
            Assert.Null(error);
            return;
        }

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, error!.Code);
        Assert.Equal(MethodId, error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(expectedReason, error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.NotEqual("Allowed", verdict);
    }
}
