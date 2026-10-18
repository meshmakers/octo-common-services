using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>Why the evaluator refused a call. For logs and tests only; the caller never learns it.</summary>
public enum CkMethodDenial
{
    /// <summary>Not refused.</summary>
    None = 0,

    /// <summary>No authenticated caller was supplied.</summary>
    NoPrincipal,

    /// <summary>The scope <c>octo_api</c> is missing.</summary>
    MissingApiScope,

    /// <summary>A scope the method declares is missing.</summary>
    MissingScope,

    /// <summary>
    ///     No declared role matched, the caller is not the target (or <c>allowSelf</c> does not apply) and the method is
    ///     not in the default-deny case.
    /// </summary>
    NotAuthorized,

    /// <summary>
    ///     The method declares no authorization or no roles and the caller is not an administrator (default-deny).
    /// </summary>
    AdministratorsOnly,

    /// <summary>The definition of the method is not known, so nothing can be verified.</summary>
    UnknownMethod
}

/// <summary>The outcome of <see cref="ICkMethodAuthorizationEvaluator.Evaluate" />.</summary>
/// <param name="Allowed">True when the call may proceed.</param>
/// <param name="Denial">The reason of a refusal; <see cref="CkMethodDenial.None" /> when allowed.</param>
public readonly record struct CkMethodAuthorizationDecision(bool Allowed, CkMethodDenial Denial)
{
    /// <summary>The call may proceed.</summary>
    public static CkMethodAuthorizationDecision Allow { get; } = new(true, CkMethodDenial.None);

    /// <summary>The call is refused for <paramref name="denial" />.</summary>
    public static CkMethodAuthorizationDecision Deny(CkMethodDenial denial) => new(false, denial);
}

/// <summary>
///     The one evaluator of the declared method authorization (<c>roles</c>, <c>allowSelf</c>, <c>scopes</c>,
///     default-deny) that the pipeline filter (this story, AB#6911) and the gateway gate (AB#6912) share. It is pure: no
///     CK cache, no I/O, no knowledge about whether the target exists.
/// </summary>
/// <remarks>
///     Rules, in this order (methods.md):
///     <list type="number">
///         <item>No caller: refused.</item>
///         <item>The scope <c>octo_api</c> is always required, then <b>all</b> declared scopes (also for administrators).</item>
///         <item><c>roles</c> are any-of, compared case-insensitively. A caller with one of them passes.</item>
///         <item>
///             <c>allowSelf</c> (instance methods with a target only) passes the caller whose <c>sub</c> equals the target
///             id. Both ids are <b>parsed</b> (<see cref="OctoObjectId" />), so upper-case and lower-case hex are equal.
///             It is honoured only when declared, also if <c>roles</c> is empty.
///         </item>
///         <item>
///             Default-deny: a method with no <c>authorization</c> or with empty <c>roles</c> admits administrators
///             (<see cref="CkMethodAuthorizationOptions.AdministratorRoles" />) and, if declared, the target itself. An
///             administrator is <b>not</b> implicitly admitted to a method that lists roles: the declaration is the
///             contract.
///         </item>
///     </list>
/// </remarks>
public interface ICkMethodAuthorizationEvaluator
{
    /// <summary>Decides whether <paramref name="principal" /> may invoke the method.</summary>
    /// <param name="declaration">The <c>authorization</c> block of the method; <c>null</c> when it declares none.</param>
    /// <param name="kind">Instance or static; <c>allowSelf</c> never applies to a static method.</param>
    /// <param name="targetRtId">The runtime id of the target as the request carries it; <c>null</c> for static methods.</param>
    /// <param name="principal">The authenticated caller; <c>null</c> when there is none.</param>
    CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
        string? targetRtId, CkMethodPrincipal? principal);
}

/// <summary>The default <see cref="ICkMethodAuthorizationEvaluator" />.</summary>
public sealed class CkMethodAuthorizationEvaluator(IOptions<CkMethodAuthorizationOptions> options)
    : ICkMethodAuthorizationEvaluator
{
    /// <inheritdoc />
    public CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
        string? targetRtId, CkMethodPrincipal? principal)
    {
        if (principal == null)
        {
            return CkMethodAuthorizationDecision.Deny(CkMethodDenial.NoPrincipal);
        }

        if (!principal.Scopes.Contains(CkMethodAuthorizationOptions.ApiScope, StringComparer.Ordinal))
        {
            return CkMethodAuthorizationDecision.Deny(CkMethodDenial.MissingApiScope);
        }

        var required = declaration?.Scopes?.Where(s => !string.IsNullOrWhiteSpace(s)) ?? [];
        if (required.Any(s => !principal.Scopes.Contains(s, StringComparer.Ordinal)))
        {
            return CkMethodAuthorizationDecision.Deny(CkMethodDenial.MissingScope);
        }

        var roles = declaration?.Roles?.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray() ?? [];
        if (roles.Length > 0 && HasAnyRole(principal, roles))
        {
            return CkMethodAuthorizationDecision.Allow;
        }

        if (declaration is { AllowSelf: true } && kind == CkMethodKindDto.Instance && IsSelf(principal, targetRtId))
        {
            return CkMethodAuthorizationDecision.Allow;
        }

        if (roles.Length == 0)
        {
            var configured = options.Value.AdministratorRoles;
            return HasAnyRole(principal, configured.Count > 0 ? configured : CkMethodAuthorizationOptions.DefaultAdministratorRoles)
                ? CkMethodAuthorizationDecision.Allow
                : CkMethodAuthorizationDecision.Deny(CkMethodDenial.AdministratorsOnly);
        }

        return CkMethodAuthorizationDecision.Deny(CkMethodDenial.NotAuthorized);
    }

    private static bool HasAnyRole(CkMethodPrincipal principal, IEnumerable<string> roles) =>
        roles.Any(r => !string.IsNullOrWhiteSpace(r) &&
                       principal.Roles.Contains(r, StringComparer.OrdinalIgnoreCase));

    private static bool IsSelf(CkMethodPrincipal principal, string? targetRtId) =>
        !string.IsNullOrWhiteSpace(principal.SubjectId) && !string.IsNullOrWhiteSpace(targetRtId) &&
        OctoObjectId.TryParse(principal.SubjectId, out var subject) &&
        OctoObjectId.TryParse(targetRtId, out var target) && subject.Equals(target);
}

/// <summary>The refusal answer, shared by the pipeline filter and the gateway gate so that both read the same.</summary>
public static class CkMethodAuthorizationErrors
{
    /// <summary>The one <see cref="CkMethodError.Parameters" /> reason of every refusal.</summary>
    public const string ReasonNotAuthorized = "not-authorized";

    /// <summary>
    ///     <c>METHOD_FORBIDDEN</c> with the method id and one fixed reason. Which rule failed, whether the target exists
    ///     or whether the method is known never shows: all refusals look the same.
    /// </summary>
    public static CkMethodError Forbidden(string methodId) =>
        CkMethodError.Create(CkMethodErrorCodes.MethodForbidden,
            (CkMethodErrorParameters.MethodId, methodId),
            (CkMethodErrorParameters.Reason, ReasonNotAuthorized));
}
