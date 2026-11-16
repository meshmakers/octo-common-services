using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     The caller-tenant check of a CK method call as one public step (AB#8776): the rule of <c>CkMethodCallerTenantRule</c>
///     (AB#8711) and the refusal the in-process transport answers. A host that does work before the invocation pipeline (the
///     method gateway of the asset repository: role check, target load, parameter validation) runs it <b>first</b>, so a token
///     of another tenant gets <c>METHOD_FORBIDDEN</c> with <c>reason = tenant-not-allowed</c> and never an answer that tells
///     whether a target exists or which check failed.
/// </summary>
public static class CkMethodCallerTenantCheck
{
    /// <summary>
    ///     <c>true</c> when the caller token may not act in <paramref name="callTenantId" />; <paramref name="error" /> is then the
    ///     refusal (<c>METHOD_FORBIDDEN</c>, parameters <c>methodId</c> and <c>reason</c> = <c>tenant-not-allowed</c>, or
    ///     <c>token-invalid</c> for a token with neither <c>sub</c> nor <c>client_id</c>). A principal that was not mapped from an
    ///     access token (<see cref="CkMethodPrincipal.IsFromAccessToken" /> false) or no principal is not judged here (the
    ///     authorization refuses a missing principal).
    /// </summary>
    /// <param name="principal">The caller.</param>
    /// <param name="callTenantId">The tenant of the call (route tenant).</param>
    /// <param name="methodId">The method id, named in the refusal.</param>
    /// <param name="tenantOptions">The cross-tenant allow-list of service clients.</param>
    /// <param name="error">The refusal.</param>
    /// <param name="verdict">The rule that refused (for the log; never a claim value).</param>
    public static bool IsRefused(CkMethodPrincipal? principal, string callTenantId, string methodId,
        TenantAuthorizationOptions tenantOptions, [NotNullWhen(true)] out CkMethodError? error, out string verdict)
    {
        ArgumentNullException.ThrowIfNull(tenantOptions);
        error = null;
        verdict = nameof(CkMethodCallerTenantVerdict.Allowed);
        if (principal is not { IsFromAccessToken: true })
        {
            return false;
        }

        var result = CkMethodCallerTenantRule.Evaluate(principal.SubjectId, principal.ClientId, principal.TokenTenantId,
            callTenantId, tenantOptions);
        verdict = result.ToString();
        if (result == CkMethodCallerTenantVerdict.Allowed)
        {
            return false;
        }

        var reason = result == CkMethodCallerTenantVerdict.NoIdentity
            ? CkMethodTokenReasons.TokenInvalid
            : CkMethodTokenReasons.TenantNotAllowed;
        error = CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
            (CkMethodErrorParameters.MethodId, methodId),
            (CkMethodErrorParameters.Reason, reason));
        return true;
    }
}
