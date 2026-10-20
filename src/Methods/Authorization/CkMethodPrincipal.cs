using System.Security.Claims;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     The authenticated caller of a CK method as the authorization sees it: who (<c>sub</c>), with which roles and which
///     OAuth scopes. It is built by the process that runs the invocation pipeline (the gateway from the validated access
///     token of the request, a job or a pipeline from its own identity) and is <b>never</b> read from the informational
///     <c>Caller</c> member of a request that arrived over the bus.
/// </summary>
/// <param name="SubjectId">The <c>sub</c> claim; <c>null</c> for client-credentials callers.</param>
/// <param name="Roles">Role names (compared case-insensitively).</param>
/// <param name="Scopes">OAuth scopes (compared ordinally, as scopes are case-sensitive).</param>
/// <param name="ClientId">
///     The <c>client_id</c> claim; only set by the token validation for remote handlers
///     (<see cref="ICkMethodCallerTokenValidator" />), <c>null</c> otherwise.
/// </param>
/// <param name="TenantId">
///     The tenant the caller was validated for; only set by the token validation for remote handlers, <c>null</c> otherwise.
/// </param>
public sealed record CkMethodPrincipal(
    string? SubjectId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes,
    string? ClientId = null,
    string? TenantId = null)
{
    /// <summary>
    ///     Keeps the three-argument constructor of earlier versions binary compatible.
    /// </summary>
    public CkMethodPrincipal(string? subjectId, IReadOnlyCollection<string> roles, IReadOnlyCollection<string> scopes)
        : this(subjectId, roles, scopes, null, null)
    {
    }

    /// <summary>
    ///     Who the caller is: the <c>sub</c> of a user, or the <c>client_id</c> of a service token (client credentials,
    ///     no <c>sub</c>). <c>null</c> when neither is known.
    /// </summary>
    public string? Subject => SubjectId ?? ClientId;

    /// <summary>
    ///     <c>true</c> for a validated token without <c>sub</c> (client credentials). Only meaningful for a principal
    ///     built by <see cref="ICkMethodCallerTokenValidator" />.
    /// </summary>
    public bool IsServiceToken => SubjectId is null && ClientId is not null;

    /// <summary>Claim types that carry roles (the OIDC <c>role</c> claim and the .NET role claim).</summary>
    private static readonly string[] RoleClaimTypes = ["role", ClaimTypes.Role];

    /// <summary>Claim types that carry the subject.</summary>
    private static readonly string[] SubjectClaimTypes = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>
    ///     Maps a validated <see cref="ClaimsPrincipal" /> (the user of an HTTP request after authentication). The
    ///     <c>scope</c> claim may hold one space-separated string or several values.
    /// </summary>
    /// <param name="principal">An authenticated principal. The method does not validate tokens.</param>
    public static CkMethodPrincipal FromClaimsPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = SubjectClaimTypes.Select(principal.FindFirst).FirstOrDefault(c => c != null)?.Value;
        var roles = RoleClaimTypes.SelectMany(principal.FindAll).Select(c => c.Value)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scopes = principal.FindAll(Infrastructure.InfrastructureCommon.ClaimScope)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new CkMethodPrincipal(subject, roles, scopes);
    }
}
