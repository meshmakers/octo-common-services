using System.Security.Claims;
using Meshmakers.Octo.Services.Methods.Authorization;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>AB#8078: <see cref="CkMethodPrincipal.FromClaimsPrincipal" /> maps <c>client_id</c> only without <c>sub</c>.</summary>
public class CkMethodPrincipalTests
{
    private static ClaimsPrincipal Token(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void AClientCredentialsToken_MapsTheClientId_AsSubject()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Token(
            new Claim("client_id", "pipeline-client"), new Claim("tenant_id", "meshtest"), new Claim("scope", "octo_api")));

        Assert.Null(principal.SubjectId);
        Assert.Equal("pipeline-client", principal.ClientId);
        Assert.Equal("pipeline-client", principal.Subject);
        Assert.True(principal.IsServiceToken);
        Assert.Equal(["octo_api"], principal.Scopes);
    }

    [Fact]
    public void AUserToken_KeepsTheClientIdUnmapped()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Token(
            new Claim("sub", "user-1"), new Claim("client_id", "octo-studio")));

        Assert.Equal("user-1", principal.SubjectId);
        Assert.Null(principal.ClientId);
        Assert.Equal("user-1", principal.Subject);
        Assert.False(principal.IsServiceToken);
    }

    [Fact]
    public void AUserTokenWithTheMappedNameIdentifier_KeepsTheClientIdUnmapped()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Token(
            new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("client_id", "octo-studio")));

        Assert.Equal("user-1", principal.SubjectId);
        Assert.Null(principal.ClientId);
    }

    [Fact]
    public void AnEmptySub_CountsAsNoSub()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Token(
            new Claim("sub", ""), new Claim("client_id", "pipeline-client")));

        Assert.Null(principal.SubjectId);
        Assert.Equal("pipeline-client", principal.ClientId);
    }

    [Fact]
    public void ATokenWithoutSubAndClientId_HasNoSubject()
    {
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Token(new Claim("scope", "octo_api")));

        Assert.Null(principal.SubjectId);
        Assert.Null(principal.ClientId);
        Assert.Null(principal.Subject);
        Assert.False(principal.IsServiceToken);
    }
}
