using System.Security.Claims;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>The evaluation matrix of the declared method authorization, without a bus (AB#6911).</summary>
public class CkMethodAuthorizationEvaluatorTests
{
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string OwnerUpper = "65F0C0FFEE65F0C0FFEE65F0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";

    private static readonly ICkMethodAuthorizationEvaluator Evaluator =
        new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions()));

    private static CkMethodPrincipal Caller(string? sub = Other, string[]? roles = null, string[]? scopes = null) =>
        new(sub, roles ?? [], scopes ?? [CkMethodAuthorizationOptions.ApiScope]);

    private static CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration,
        CkMethodPrincipal? principal, string? target = Owner, CkMethodKindDto kind = CkMethodKindDto.Instance) =>
        Evaluator.Evaluate(declaration, kind, target, principal);

    private static CkMethodAuthorizationDto Roles(params string[] roles) => new() { Roles = [.. roles] };

    // ---- roles ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("AssetManagement")]
    [InlineData("assetmanagement")]
    [InlineData("ASSETMANAGEMENT")]
    public void Roles_CallerWithTheRoleInAnyCase_Passes(string callerRole)
    {
        var decision = Evaluate(Roles("AssetManagement"), Caller(roles: [callerRole]));

        Assert.True(decision.Allowed);
        Assert.Equal(CkMethodDenial.None, decision.Denial);
    }

    [Fact]
    public void Roles_AreAnyOf()
    {
        Assert.True(Evaluate(Roles("A", "B", "C"), Caller(roles: ["C"])).Allowed);
    }

    [Fact]
    public void Roles_CallerWithoutTheRole_IsRefused()
    {
        var decision = Evaluate(Roles("AssetManagement"), Caller(roles: ["Other"]));

        Assert.False(decision.Allowed);
        Assert.Equal(CkMethodDenial.NotAuthorized, decision.Denial);
    }

    [Fact]
    public void Roles_AdministratorIsNotImplicitlyAdmitted_WhenTheMethodListsRoles()
    {
        // the declaration is the contract: default-deny is for methods that declare nothing
        Assert.False(Evaluate(Roles("AssetManagement"), Caller(roles: ["AdminPanelManagement"])).Allowed);
    }

    // ---- allowSelf ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Owner, Owner)]
    [InlineData(Owner, OwnerUpper)]
    [InlineData(OwnerUpper, Owner)]
    public void AllowSelf_SubEqualsTarget_PassesAlsoWhenTheLetterCaseDiffers(string sub, string target)
    {
        var declaration = new CkMethodAuthorizationDto { AllowSelf = true };

        Assert.True(Evaluate(declaration, Caller(sub), target).Allowed);
    }

    [Fact]
    public void AllowSelf_AnotherCaller_IsRefused()
    {
        var declaration = new CkMethodAuthorizationDto { Roles = ["UserManagement"], AllowSelf = true };

        var decision = Evaluate(declaration, Caller(Other), Owner);

        Assert.False(decision.Allowed);
        Assert.Equal(CkMethodDenial.NotAuthorized, decision.Denial);
    }

    [Fact]
    public void AllowSelf_IsHonoured_AlsoWithEmptyRoles_AndAdministratorsStillPass()
    {
        var declaration = new CkMethodAuthorizationDto { AllowSelf = true };

        Assert.True(Evaluate(declaration, Caller(Owner)).Allowed);
        Assert.True(Evaluate(declaration, Caller(Other, ["AdminPanelManagement"])).Allowed);
        Assert.False(Evaluate(declaration, Caller(Other)).Allowed);
    }

    [Fact]
    public void AllowSelf_DoesNotApplyToAStaticMethodOrWithoutATarget()
    {
        var declaration = new CkMethodAuthorizationDto { AllowSelf = true };

        Assert.False(Evaluate(declaration, Caller(Owner), Owner, CkMethodKindDto.Static).Allowed);
        Assert.False(Evaluate(declaration, Caller(Owner), target: null).Allowed);
    }

    [Theory]
    [InlineData(null, Owner)]
    [InlineData("", Owner)]
    [InlineData("not-an-object-id", "not-an-object-id")] // equal strings, but not ids: no match
    [InlineData(Owner, "not-an-object-id")]
    public void AllowSelf_UnparsableOrMissingIds_NeverMatch(string? sub, string target)
    {
        var declaration = new CkMethodAuthorizationDto { AllowSelf = true };

        Assert.False(Evaluate(declaration, Caller(sub), target).Allowed);
    }

    // ---- scopes ---------------------------------------------------------------------------------------------

    [Fact]
    public void Scopes_AllDeclaredScopesAreRequired()
    {
        var declaration = new CkMethodAuthorizationDto { Roles = ["R"], Scopes = ["assets:write", "audit"] };

        Assert.True(Evaluate(declaration, Caller(roles: ["R"], scopes: ["octo_api", "assets:write", "audit"])).Allowed);

        var decision = Evaluate(declaration, Caller(roles: ["R"], scopes: ["octo_api", "assets:write"]));
        Assert.False(decision.Allowed);
        Assert.Equal(CkMethodDenial.MissingScope, decision.Denial);
    }

    [Fact]
    public void Scopes_ApplyToAdministratorsAndOwnersToo()
    {
        var declaration = new CkMethodAuthorizationDto { AllowSelf = true, Scopes = ["audit"] };

        Assert.False(Evaluate(declaration, Caller(Owner, ["AdminPanelManagement"])).Allowed);
    }

    [Theory]
    [MemberData(nameof(Declarations))]
    public void ApiScope_IsAlwaysRequired_EvenWhenTheMethodListsNoScope(CkMethodAuthorizationDto? declaration)
    {
        var caller = Caller(Owner, ["AssetManagement", "AdminPanelManagement"], scopes: ["assets:write"]);

        var decision = Evaluate(declaration, caller);

        Assert.False(decision.Allowed);
        Assert.Equal(CkMethodDenial.MissingApiScope, decision.Denial);
    }

    public static IEnumerable<object?[]> Declarations() =>
    [
        [null],
        [new CkMethodAuthorizationDto()],
        [Roles("AssetManagement")],
        [new CkMethodAuthorizationDto { AllowSelf = true }]
    ];

    [Fact]
    public void Scopes_AreCaseSensitive()
    {
        Assert.False(Evaluate(Roles("R"), Caller(roles: ["R"], scopes: ["OCTO_API"])).Allowed);
    }

    // ---- default-deny ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NothingDeclared))]
    public void DefaultDeny_OnlyAnAdministratorPasses(CkMethodAuthorizationDto? declaration)
    {
        Assert.True(Evaluate(declaration, Caller(roles: ["AdminPanelManagement"])).Allowed);
        Assert.True(Evaluate(declaration, Caller(roles: ["adminpanelmanagement"])).Allowed);

        var other = Evaluate(declaration, Caller(roles: ["AssetManagement", "UserManagement"]));
        Assert.False(other.Allowed);
        Assert.Equal(CkMethodDenial.AdministratorsOnly, other.Denial);
    }

    [Theory]
    [MemberData(nameof(NothingDeclared))]
    public void DefaultDeny_TheOwnerOfTheTargetIsRefused(CkMethodAuthorizationDto? declaration)
    {
        Assert.False(Evaluate(declaration, Caller(Owner), Owner).Allowed);
    }

    public static IEnumerable<object?[]> NothingDeclared() =>
    [
        [null],
        [new CkMethodAuthorizationDto()],
        [new CkMethodAuthorizationDto { Roles = [] }],
        [new CkMethodAuthorizationDto { Roles = ["", "  "] }],
        [new CkMethodAuthorizationDto { Scopes = [] }]
    ];

    [Fact]
    public void DefaultDeny_AdministratorRolesAreConfigurable()
    {
        var evaluator = new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions
        {
            AdministratorRoles = ["Root"]
        }));

        Assert.True(evaluator.Evaluate(null, CkMethodKindDto.Instance, Owner, Caller(roles: ["Root"])).Allowed);
        Assert.False(evaluator.Evaluate(null, CkMethodKindDto.Instance, Owner, Caller(roles: ["AdminPanelManagement"]))
            .Allowed);
    }

    [Fact]
    public void NoPrincipal_IsRefused_EvenForAMethodThatDeclaresNothingToCheck()
    {
        var decision = Evaluate(Roles("R"), principal: null);

        Assert.False(decision.Allowed);
        Assert.Equal(CkMethodDenial.NoPrincipal, decision.Denial);
    }

    // ---- principal mapping ----------------------------------------------------------------------------------

    [Fact]
    public void FromClaimsPrincipal_MapsSubjectRolesAndScopes()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", Owner), new Claim("role", "A"), new Claim(ClaimTypes.Role, "a"), new Claim("role", "B"),
            new Claim("scope", "octo_api assets:write"), new Claim("scope", "audit")
        ], "test");

        var principal = CkMethodPrincipal.FromClaimsPrincipal(new ClaimsPrincipal(identity));

        Assert.Equal(Owner, principal.SubjectId);
        Assert.Equal(["A", "B"], principal.Roles.OrderBy(r => r));
        Assert.Equal(["assets:write", "audit", "octo_api"], principal.Scopes.OrderBy(s => s));
    }

    [Fact]
    public void PrincipalScope_FlowsIntoAsyncCalls_AndRestoresThePreviousPrincipal()
    {
        var outer = Caller("o");
        var inner = Caller("i");
        Assert.Null(CkMethodPrincipalScope.CurrentPrincipal);

        using (CkMethodPrincipalScope.Begin(outer))
        {
            using (CkMethodPrincipalScope.Begin(inner))
            {
                Assert.Same(inner, CkMethodPrincipalScope.CurrentPrincipal);
            }

            Assert.Same(outer, CkMethodPrincipalScope.CurrentPrincipal);
        }

        Assert.Null(CkMethodPrincipalScope.CurrentPrincipal);
    }
}
