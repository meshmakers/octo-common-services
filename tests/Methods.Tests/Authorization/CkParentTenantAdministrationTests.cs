using System.Security.Claims;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Tests.Authorization.Token;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;
using static Meshmakers.Octo.Services.Methods.Tests.Authorization.ParentTenantAdministrationKit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     The parent-tenant administration rule of CK method calls: a user token of a parent tenant may call a method of the
///     closed list <see cref="CkParentTenantAdministrationMethods" /> on a registered child tenant, on the gateway check
///     (<see cref="CkMethodCallerTenantCheck.CheckAsync" />), the in-process transport (<see cref="CkMethodCallerTenantFilter" />)
///     and the bus host (<see cref="ICkMethodCallerTokenValidator.ValidateForMethodAsync" />). Everything else keeps the exact
///     match.
/// </summary>
public class CkParentTenantAdministrationTests
{
    private const string Dump = CkParentTenantAdministrationMethods.TenantRepositoryDump;
    private const string OtherMethod = "System.Identity/User.ChangePassword-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- the closed list

    [Fact]
    public void ClosedList_HoldsExactlyTheFourRepositoryMethods()
    {
        Assert.Equal(
        [
            "System.Bot/TenantRepository.Dump-1",
            "System.Bot/TenantRepository.ExportArchiveData-1",
            "System.Bot/TenantRepository.ImportArchiveData-1",
            "System.Bot/TenantRepository.Restore-1"
        ], CkParentTenantAdministrationMethods.MethodIds.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("System.Bot/TenantRepository.Dump-1", true)]
    [InlineData("System.Bot/TenantRepository.Dump-2", false)]
    [InlineData("system.bot/tenantrepository.dump-1", false)]
    [InlineData(" System.Bot/TenantRepository.Dump-1", false)]
    [InlineData("System.Bot/Tenant.Dump-1", false)]
    [InlineData("System.Identity/User.ChangePassword-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ClosedList_MatchesOrdinally(string? methodId, bool expected) =>
        Assert.Equal(expected, CkParentTenantAdministrationMethods.Contains(methodId));

    // ---------------------------------------------------------------- the rule

    public static TheoryData<string, string, string, bool, string, string> RuleCases() => new()
    {
        // because, token tenant, call tenant, user token, method, expected verdict
        { "own tenant", Parent, Parent, true, Dump, nameof(CkMethodCallerTenantVerdict.Allowed) },
        { "parent on its child", Parent, Child, true, Dump, nameof(CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator) },
        { "parent on its child, other case", "PARENT1", "Child1", true, Dump, nameof(CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator) },
        { "child on its sibling", Child, Sibling, true, Dump, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "parent on a tenant that is not its child", Parent, Unrelated, true, Dump, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "parent on its grandchild", Parent, Grandchild, true, Dump, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "child on its parent", Child, Parent, true, Dump, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "parent on a deleted or unregistered child", Parent, "gone1", true, Dump, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "system tenant on a grandchild", SystemTenant, Grandchild, true, Dump, nameof(CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator) },
        { "parent on its child, method not on the list", Parent, Child, true, OtherMethod, nameof(CkMethodCallerTenantVerdict.UserTenantMismatch) },
        { "service token of the parent on its child", Parent, Child, false, Dump, nameof(CkMethodCallerTenantVerdict.ServiceTenantMismatch) },
        { "service token of the system tenant", SystemTenant, Child, false, Dump, nameof(CkMethodCallerTenantVerdict.ServiceTenantMismatch) }
    };

    [Theory]
    [MemberData(nameof(RuleCases))]
    public async Task EvaluateAsync_FollowsTheRule(string because, string tokenTenant, string callTenant, bool user,
        string methodId, string expected)
    {
        var verdict = await CkMethodCallerTenantRule.EvaluateAsync(user ? "u1" : null, user ? "studio" : "svc",
            tokenTenant, callTenant, methodId, EnforcingOptions(), new Hierarchy(), NullLogger.Instance, "test");

        Assert.True(expected == verdict.ToString(), $"{because}: expected {expected}, got {verdict}");
    }

    [Fact]
    public async Task EvaluateAsync_ServiceTokenOrOtherMethod_NeverReadsTheHierarchy()
    {
        var hierarchy = new Hierarchy();

        await CkMethodCallerTenantRule.EvaluateAsync(null, "svc", Parent, Child, Dump, EnforcingOptions(), hierarchy,
            null, "test");
        await CkMethodCallerTenantRule.EvaluateAsync("u1", null, Parent, Child, OtherMethod, EnforcingOptions(),
            hierarchy, null, "test");
        await CkMethodCallerTenantRule.EvaluateAsync("u1", null, null, Child, Dump, EnforcingOptions(), hierarchy, null,
            "test");

        Assert.Equal(0, hierarchy.Reads);
    }

    [Fact]
    public async Task EvaluateAsync_WithoutReader_OrWithAFailingReader_Refuses()
    {
        var withoutReader = await CkMethodCallerTenantRule.EvaluateAsync("u1", null, Parent, Child, Dump,
            EnforcingOptions(), null, null, "test");
        var failingReader = await CkMethodCallerTenantRule.EvaluateAsync("u1", null, Parent, Child, Dump,
            EnforcingOptions(), new Hierarchy { Throws = true }, NullLogger.Instance, "test");

        Assert.Equal(CkMethodCallerTenantVerdict.UserTenantMismatch, withoutReader);
        Assert.Equal(CkMethodCallerTenantVerdict.UserTenantMismatch, failingReader);
    }

    [Fact]
    public async Task EvaluateAsync_LogsTheGrantAtInformation_WithoutTokenOrParameters()
    {
        var logs = new FakeLogCollector();
        var logger = new FakeLogger(logs);

        await CkMethodCallerTenantRule.EvaluateAsync("u1", "studio", Parent, Child, Dump, EnforcingOptions(),
            new Hierarchy(), logger, "gateway");

        var record = Assert.Single(logs.GetSnapshot());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, record.Level);
        foreach (var expected in new[] { "u1", "studio", Parent, Child, Dump, "gateway" })
        {
            Assert.Contains(expected, record.Message);
        }
    }

    // ---------------------------------------------------------------- gateway check

    [Fact]
    public async Task CheckAsync_ParentOnChild_IsAllowedAndMarked()
    {
        var result = await CkMethodCallerTenantCheck.CheckAsync(UserPrincipal(Parent), Child, Dump, EnforcingOptions(),
            new Hierarchy(), null, "gateway");

        Assert.False(result.IsRefused);
        Assert.True(result.IsParentTenantAdministration);
    }

    [Fact]
    public async Task CheckAsync_ParentOnChild_OtherMethod_IsTheSameRefusalAsToday()
    {
        var result = await CkMethodCallerTenantCheck.CheckAsync(UserPrincipal(Parent), Child, OtherMethod,
            EnforcingOptions(), new Hierarchy(), null, "gateway");
        var today = CkMethodCallerTenantCheck.IsRefused(UserPrincipal(Parent), Child, OtherMethod, EnforcingOptions(),
            out var todayError, out _);

        Assert.True(result.IsRefused);
        Assert.True(today);
        Assert.Equal(todayError!.Code, result.Error.Code);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, result.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(OtherMethod, result.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public void IsRefused_WithoutMethodContext_KeepsTheExactMatch()
    {
        // the synchronous check has no hierarchy reader: a parent token stays refused there
        Assert.True(CkMethodCallerTenantCheck.IsRefused(UserPrincipal(Parent), Child, Dump, EnforcingOptions(), out _,
            out _));
    }

    // ---------------------------------------------------------------- in-process transport

    [Theory]
    [InlineData(Child, Dump, CurrentGeneration, null)]
    [InlineData(Child, OtherMethod, CurrentGeneration, CkMethodErrorCodes.MethodForbidden)]
    [InlineData(Grandchild, Dump, CurrentGeneration, CkMethodErrorCodes.MethodForbidden)]
    [InlineData(Child, Dump, OldGeneration, CkMethodErrorCodes.MethodForbidden)]
    // the generation is the parent's, never the child's: a parent token carrying the child's generation is refused
    [InlineData(Child, Dump, ChildGeneration, CkMethodErrorCodes.MethodForbidden)]
    public async Task InProcess_ParentRule(string callTenant, string methodId, string generation, string? expectedError)
    {
        var options = EnforcingOptions();
        var filter = new CkMethodCallerTenantFilter(Options.Create(options),
            NullLogger<CkMethodCallerTenantFilter>.Instance, generationCheck: GenerationCheck(options),
            hierarchyReader: new Hierarchy());
        var passed = false;
        var request = TestData.Request(DateTime.UtcNow.AddSeconds(30), callTenant) with { MethodId = methodId };

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(UserPrincipal(Parent, generation)))
        {
            response = await filter.InvokeAsync(
                new CkMethodInvocationContext(CkMethodTransportNames.InProcess, request, Ct),
                r =>
                {
                    passed = true;
                    return Task.FromResult(CkMethodResponse.Succeeded(r.InvocationId, "h", "null"));
                });
        }

        Assert.Equal(expectedError is null, passed);
        Assert.Equal(expectedError, response.Error?.Code);
    }

    // ---------------------------------------------------------------- bus host

    [Theory]
    [InlineData(Child, Dump, true, CurrentGeneration, true)]
    [InlineData(Child, OtherMethod, true, CurrentGeneration, false)]
    [InlineData(Sibling, Dump, true, CurrentGeneration, true)]
    [InlineData(Grandchild, Dump, true, CurrentGeneration, false)]
    [InlineData(Child, Dump, false, CurrentGeneration, false)]
    [InlineData(Child, Dump, true, OldGeneration, false)]
    [InlineData(Child, Dump, true, ChildGeneration, false)]
    public async Task BusHost_ParentRule(string callTenant, string methodId, bool user, string generation, bool valid)
    {
        var options = EnforcingOptions();
        using var f = new ValidatorFixture(tenantOptions: options, generationCheck: GenerationCheck(options),
            hierarchyReader: new Hierarchy());
        var token = TokenFactory.Create(f.Key, claims => claims[TenantGenerationClaim.ClaimType] = generation,
            user: user, tenant: Parent);

        var result = await f.Validator.ValidateForMethodAsync(token, callTenant, methodId, Ct);

        Assert.Equal(valid, result.IsValid);
        if (valid)
        {
            Assert.Equal(callTenant, result.Principal!.TenantId);
            Assert.Equal(Parent, result.Principal.TokenTenantId);
        }
        else
        {
            Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, result.Reason);
        }
    }

    [Fact]
    public async Task BusHost_ValidateWithoutMethod_KeepsTheExactMatch()
    {
        using var f = new ValidatorFixture(tenantOptions: EnforcingOptions(), hierarchyReader: new Hierarchy());
        var token = TokenFactory.Create(f.Key, tenant: Parent);

        var result = await f.Validate(token, Child);

        Assert.False(result.IsValid);
        Assert.Equal(CkMethodTokenReasons.TenantNotAllowed, result.Reason);
    }

    [Fact]
    public async Task BusHost_RequestExtension_PassesTheMethodId()
    {
        using var f = new ValidatorFixture(tenantOptions: EnforcingOptions(), hierarchyReader: new Hierarchy());
        var request = TestData.Request(DateTime.UtcNow.AddSeconds(30), Child) with
        {
            MethodId = Dump,
            CallerAccessToken = TokenFactory.Create(f.Key, tenant: Parent)
        };

        var result = await f.Validator.ValidateAsync(request, Ct);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task DefaultInterfaceImplementation_HasNoParentRule()
    {
        ICkMethodCallerTokenValidator validator = new ExactOnlyValidator();

        var result = await validator.ValidateForMethodAsync("t", Child, Dump, Ct);

        Assert.False(result.IsValid);
    }

    private static CkMethodPrincipal UserPrincipal(string tenant, string generation = CurrentGeneration) =>
        CkMethodPrincipal.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "0123456789abcdef01234567"),
            new Claim("tenant_id", tenant),
            new Claim("scope", CkMethodAuthorizationOptions.ApiScope),
            new Claim("role", CkParentTenantAdministrationMethods.RequiredRole),
            new Claim(TenantGenerationClaim.ClaimType, generation)
        ], "Bearer")));

    /// <summary>An implementation from before the method-aware overload: only the exact-match member.</summary>
    private sealed class ExactOnlyValidator : ICkMethodCallerTokenValidator
    {
        public ValueTask<CkMethodTokenValidationResult> ValidateAsync(string? accessToken, string expectedTenantId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CkMethodTokenValidationResult.Refused(CkMethodTokenReasons.TenantNotAllowed));
    }
}
