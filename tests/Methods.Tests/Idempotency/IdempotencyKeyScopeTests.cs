using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Idempotency;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Idempotency;

/// <summary>AB#8078: the idempotency scope of a caller (decided by Odin in AB#7829).</summary>
public class IdempotencyKeyScopeTests
{
    [Fact]
    public void AUser_IsScopedByItsSub()
    {
        Assert.Equal("user-1", CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal("user-1", [], [])));
    }

    [Fact]
    public void AUserWithAClientId_IsStillScopedByItsSub()
    {
        // the remote-handler token validation sets ClientId for user tokens too
        Assert.Equal("user-1", CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal("user-1", [], [], "studio")));
    }

    [Fact]
    public void AClientCredentialsCaller_IsScopedByThePrefixedClientId()
    {
        Assert.Equal("client:pipeline-client",
            CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal(null, [], [], "pipeline-client")));
    }

    [Fact]
    public void ACallerWithNeitherSubNorClientId_HasNoScope()
    {
        Assert.Null(CkMethodIdempotencyKey.ScopeOf(null));
        Assert.Null(CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal(null, [], [])));
        Assert.Null(CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal(" ", [], [], " ")));
    }

    [Fact]
    public void AClientAndAUserWhoseSubEqualsTheClientId_HaveDifferentScopesAndKeys()
    {
        var client = CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal(null, [], [], "shared-name"))!;
        var user = CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal("shared-name", [], []))!;

        Assert.NotEqual(client, user);
        Assert.NotEqual(
            CkMethodIdempotencyKey.Create("meshtest", "Type-1/Method-1", client, "key-1").Id,
            CkMethodIdempotencyKey.Create("meshtest", "Type-1/Method-1", user, "key-1").Id);
    }

    [Fact]
    public void AUserWhoseSubStartsWithTheClientPrefix_HasNoScope_SoItCanNeverShareAClientScope()
    {
        // review finding PR #99: sub "client:pipeline-client" must not land in the scope of client "pipeline-client"
        Assert.Null(CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal("client:pipeline-client", [], [])));
        Assert.Equal("client:pipeline-client",
            CkMethodIdempotencyKey.ScopeOf(new CkMethodPrincipal(null, [], [], "pipeline-client")));
    }

    [Fact]
    public void TheAuditSubjectOfAClient_StaysThePlainClientId()
    {
        var principal = new CkMethodPrincipal(null, [], [], "pipeline-client");

        Assert.Equal("pipeline-client", principal.Subject);
        Assert.True(principal.IsServiceToken);
    }
}
