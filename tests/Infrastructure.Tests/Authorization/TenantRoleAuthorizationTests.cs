using System.Security.Claims;
using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using FakeItEasy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.Tests.Authorization;

/// <summary>
///     AB#7319: the central "full-access scope plus tenant role" policy and the <c>403</c> body it produces.
/// </summary>
public class TenantRoleAuthorizationTests
{
    private const string PolicyName = "TenantManagementPolicy";

    private static ServiceProvider CreateServices(bool registerHandlers = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddTenantRolePolicy(PolicyName, CommonConstants.TenantManagementRole));
        if (registerHandlers)
        {
            services.AddOctoTenantRoleAuthorization();
        }

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(string? scope, params (string Type, string Value)[] roles)
    {
        var claims = new List<Claim>();
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        claims.AddRange(roles.Select(r => new Claim(r.Type, r.Value)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static async Task<AuthorizationResult> AuthorizeAsync(ServiceProvider sp, ClaimsPrincipal user)
    {
        return await sp.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, PolicyName);
    }

    [Fact]
    public async Task ScopeAndRole_RawRoleClaim_Succeeds()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess, ("role", "TenantManagement")));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ScopeAndRole_MappedRoleClaim_Succeeds()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp,
            User(CommonConstants.OctoApiFullAccess, (ClaimTypes.Role, "TenantManagement")));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ScopeWithoutRole_IsRefusedWithAReason()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess));

        Assert.False(result.Succeeded);
        var reason = Assert.Single(result.Failure!.FailureReasons);
        Assert.Contains("TenantManagement", reason.Message);
    }

    [Fact]
    public async Task OtherRole_IsRefused()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess, ("role", "UserManagement")));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task RoleWithoutScope_IsRefusedWithTheScopeReason()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiReadOnly, ("role", "TenantManagement")));

        Assert.False(result.Succeeded);
        var message = Assert.Single(result.Failure!.FailureReasons).Message;
        Assert.Contains(CommonConstants.OctoApiFullAccess, message);
        Assert.DoesNotContain("role", message);
    }

    [Fact]
    public async Task ReadOnlyScopeWithoutRole_NamesTheScope_NotTheRole()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiReadOnly));

        var message = Assert.Single(result.Failure!.FailureReasons).Message;
        Assert.Contains($"scope '{CommonConstants.OctoApiFullAccess}'", message);
        Assert.DoesNotContain("TenantManagement", message);
    }

    [Fact]
    public async Task NoScopeClaimAtAll_IsRefusedWithTheScopeReason()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(null, ("role", "TenantManagement")));

        Assert.Contains($"scope '{CommonConstants.OctoApiFullAccess}'", Assert.Single(result.Failure!.FailureReasons).Message);
    }

    [Fact]
    public async Task ResultHandler_WritesTheScopeReasonIntoTheBody_WhenOnlyTheRoleIsPresent()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiReadOnly, ("role", "TenantManagement")));
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await sp.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ => Task.CompletedTask,
            context,
            new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build(),
            PolicyAuthorizationResult.Forbid(result.Failure));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(CommonConstants.OctoApiFullAccess, json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RoleNameIsCaseSensitive()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess, ("role", "tenantmanagement")));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task WithoutRegisteredHandlers_EveryRequestIsRefused()
    {
        await using var sp = CreateServices(false);
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess, ("role", "TenantManagement")));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ResultHandler_WritesTheReasonAs403JsonMessage()
    {
        await using var sp = CreateServices();
        var result = await AuthorizeAsync(sp, User(CommonConstants.OctoApiFullAccess));
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var nextCalled = false;

        await sp.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            context,
            new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build(),
            PolicyAuthorizationResult.Forbid(result.Failure));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("TenantManagement", json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ResultHandler_ForForeignForbiddenReason_KeepsTheDefaultBareForbidden()
    {
        await using var sp = CreateServices();
        var authentication = A.Fake<IAuthenticationService>();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider()
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await sp.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ => Task.CompletedTask,
            context,
            new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build(),
            PolicyAuthorizationResult.Forbid());

        // The default handler challenged/forbade through the authentication service; our handler wrote no body.
        A.CallTo(authentication).Where(c => c.Method.Name == nameof(IAuthenticationService.ForbidAsync))
            .MustHaveHappenedOnceExactly();
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task ResultHandler_OnSuccess_CallsNext()
    {
        await using var sp = CreateServices();
        var nextCalled = false;

        await sp.GetRequiredService<IAuthorizationMiddlewareResultHandler>().HandleAsync(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new DefaultHttpContext(),
            new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build(),
            PolicyAuthorizationResult.Success());

        Assert.True(nextCalled);
    }
}
