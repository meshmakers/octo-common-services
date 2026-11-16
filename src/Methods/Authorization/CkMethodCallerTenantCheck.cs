using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;

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
    ///     The check with the parent-tenant administration rule (<see cref="CkParentTenantAdministrationMethods" />): like
    ///     <see cref="IsRefused" />, but a user token issued for a parent tenant may call a method of the closed list on a
    ///     registered child tenant (<see cref="ITenantHierarchyReader.IsChildTenantAsync" />, fail-closed). Hosts that know the
    ///     method id of the call use this one; without <paramref name="hierarchyReader" /> it decides exactly like
    ///     <see cref="IsRefused" />.
    /// </summary>
    /// <param name="principal">The caller.</param>
    /// <param name="callTenantId">The tenant of the call (route tenant).</param>
    /// <param name="methodId">The method id, named in the refusal and matched against the closed list.</param>
    /// <param name="tenantOptions">The cross-tenant allow-list of service clients.</param>
    /// <param name="hierarchyReader">The cached tenant hierarchy; <c>null</c> = no parent-tenant rule.</param>
    /// <param name="logger">Logs a parent-tenant grant at Information; <c>null</c> = not logged.</param>
    /// <param name="transport">Names the place of the check in the grant log.</param>
    public static async ValueTask<CkMethodCallerTenantCheckResult> CheckAsync(CkMethodPrincipal? principal,
        string callTenantId, string methodId, TenantAuthorizationOptions tenantOptions,
        ITenantHierarchyReader? hierarchyReader, ILogger? logger, string transport)
    {
        ArgumentNullException.ThrowIfNull(tenantOptions);
        if (principal is not { IsFromAccessToken: true })
        {
            return CkMethodCallerTenantCheckResult.Allow(nameof(CkMethodCallerTenantVerdict.Allowed), false);
        }

        // the client of a user token sits in TokenClientId (ClientId stays null for users); the rule only logs it for users
        var result = await CkMethodCallerTenantRule.EvaluateAsync(principal.SubjectId,
            principal.TokenClientId ?? principal.ClientId, principal.TokenTenantId, callTenantId, methodId, tenantOptions,
            hierarchyReader, logger, transport)
            .ConfigureAwait(false);
        return result.IsAllowed()
            ? CkMethodCallerTenantCheckResult.Allow(result.ToString(),
                result == CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator)
            : CkMethodCallerTenantCheckResult.Refuse(Refusal(result, methodId), result.ToString());
    }

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

        error = Refusal(result, methodId);
        return true;
    }

    private static CkMethodError Refusal(CkMethodCallerTenantVerdict verdict, string methodId)
    {
        var reason = verdict == CkMethodCallerTenantVerdict.NoIdentity
            ? CkMethodTokenReasons.TokenInvalid
            : CkMethodTokenReasons.TenantNotAllowed;
        return CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
            (CkMethodErrorParameters.MethodId, methodId),
            (CkMethodErrorParameters.Reason, reason));
    }
}

/// <summary>The outcome of <see cref="CkMethodCallerTenantCheck.CheckAsync" />.</summary>
public sealed class CkMethodCallerTenantCheckResult
{
    private CkMethodCallerTenantCheckResult(CkMethodError? error, string verdict, bool isParentTenantAdministration)
    {
        Error = error;
        Verdict = verdict;
        IsParentTenantAdministration = isParentTenantAdministration;
    }

    /// <summary><c>true</c> when the caller may not act in the tenant of the call; <see cref="Error" /> is then set.</summary>
    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsRefused => Error is not null;

    /// <summary>The refusal (<c>METHOD_FORBIDDEN</c> with <c>methodId</c> and <c>reason</c>), or <c>null</c>.</summary>
    public CkMethodError? Error { get; }

    /// <summary>The rule that decided, for the log; never a claim value.</summary>
    public string Verdict { get; }

    /// <summary><c>true</c> when the call is allowed by the parent-tenant administration rule.</summary>
    public bool IsParentTenantAdministration { get; }

    internal static CkMethodCallerTenantCheckResult Allow(string verdict, bool isParentTenantAdministration) =>
        new(null, verdict, isParentTenantAdministration);

    internal static CkMethodCallerTenantCheckResult Refuse(CkMethodError error, string verdict) =>
        new(error, verdict, false);
}
