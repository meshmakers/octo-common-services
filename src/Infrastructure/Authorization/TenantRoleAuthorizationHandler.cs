using System.Security.Claims;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Evaluates a <see cref="TenantRoleRequirement" /> (AB#7319). A token without the scope or without the role fails the
///     requirement with a reason naming the missing one, which <see cref="ForbiddenReasonAuthorizationResultHandler" /> writes into
///     the <c>403</c> body.
/// </summary>
public sealed class TenantRoleAuthorizationHandler : AuthorizationHandler<TenantRoleRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRoleRequirement requirement)
    {
        if (!HasFullAccessScope(context.User))
        {
            context.Fail(new AuthorizationFailureReason(this,
                $"Access is denied: this operation requires the scope '{CommonConstants.OctoApiFullAccess}'. " +
                "The token was issued without it; request a token with full API access."));
        }
        else if (!HasTenantRole(context.User, requirement.RoleName))
        {
            context.Fail(new AuthorizationFailureReason(this,
                $"Access is denied: this operation requires the role '{requirement.RoleName}' in the tenant. " +
                "The token carries no such role; ask a tenant administrator to grant it."));
        }
        else
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether the principal carries the full-access scope (the same test as <c>RequireClaim("scope", "octo_api")</c>).
    /// </summary>
    public static bool HasFullAccessScope(ClaimsPrincipal principal)
    {
        return principal.HasClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess);
    }

    /// <summary>
    ///     Whether the principal carries the tenant role under the identity's role claim type, the raw JWT <c>role</c> claim or
    ///     the inbound-mapped <see cref="ClaimTypes.Role" /> claim (the JWT bearer handler may rename the former to the latter,
    ///     so both spellings are probed - platform convention).
    /// </summary>
    public static bool HasTenantRole(ClaimsPrincipal principal, string roleName)
    {
        return principal.IsInRole(roleName) ||
               principal.Claims.Any(c =>
                   (c.Type == RawRoleClaimType || c.Type == ClaimTypes.Role) &&
                   string.Equals(c.Value, roleName, StringComparison.Ordinal));
    }

    private const string RawRoleClaimType = "role";
}
