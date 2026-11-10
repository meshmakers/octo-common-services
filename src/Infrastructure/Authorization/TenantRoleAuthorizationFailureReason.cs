using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     The reason of a refused <see cref="TenantRoleRequirement" /> (AB#7319), with the requirement's stable error code
///     (AB#8529) that <see cref="ForbiddenReasonAuthorizationResultHandler" /> writes into the <c>403</c> body.
/// </summary>
public sealed class TenantRoleAuthorizationFailureReason : AuthorizationFailureReason
{
    /// <summary>
    ///     Creates the reason.
    /// </summary>
    /// <param name="handler">The handler that refused the requirement.</param>
    /// <param name="message">The reason, naming the missing scope or role and the remedy.</param>
    /// <param name="code">The stable error code of the requirement, or <c>null</c>.</param>
    public TenantRoleAuthorizationFailureReason(IAuthorizationHandler handler, string message, string? code)
        : base(handler, message)
    {
        Code = code;
    }

    /// <summary>
    ///     The stable error code of the refused requirement (<see cref="TenantRoleRequirement.ErrorCode" />), or <c>null</c>.
    /// </summary>
    public string? Code { get; }
}
