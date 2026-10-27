using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Requires the caller's token to carry a role of the tenant it was issued for (AB#7319).
/// </summary>
/// <remarks>
///     Use <see cref="TenantRoleAuthorizationExtensions.AddTenantRolePolicy" /> instead of adding the requirement by hand:
///     it pairs the role with the full-access scope.
/// </remarks>
/// <param name="RoleName">The role the token must carry, e.g. <c>TenantManagement</c>.</param>
public sealed record TenantRoleRequirement(string RoleName) : IAuthorizationRequirement;
