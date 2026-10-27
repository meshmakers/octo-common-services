using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Requires the full-access scope <c>octo_api</c> and a role of the tenant in the caller's token (AB#7319).
/// </summary>
/// <remarks>
///     Scope and role are one requirement on purpose: the handler can then name whichever of the two is missing in the
///     <c>403</c> reason. Use <see cref="TenantRoleAuthorizationExtensions.AddTenantRolePolicy" /> instead of adding the
///     requirement by hand.
/// </remarks>
/// <param name="RoleName">The role the token must carry, e.g. <c>TenantManagement</c>.</param>
public sealed record TenantRoleRequirement(string RoleName) : IAuthorizationRequirement;
