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
///     The <c>client_id</c> claim. <see cref="FromClaimsPrincipal" /> sets it only for a token without <c>sub</c> (client
///     credentials, AB#8078), so a service caller has a <see cref="Subject" /> at the gateway too; the token validation for
///     remote handlers (<see cref="ICkMethodCallerTokenValidator" />) sets it whenever the token carries one.
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
    ///     <c>true</c> for a token without <c>sub</c> but with a <c>client_id</c> (client credentials), as mapped by
    ///     <see cref="FromClaimsPrincipal" /> or <see cref="ICkMethodCallerTokenValidator" />.
    /// </summary>
    public bool IsServiceToken => SubjectId is null && ClientId is not null;

    /// <summary>Claim types that carry roles (the OIDC <c>role</c> claim and the .NET role claim).</summary>
    private static readonly string[] RoleClaimTypes = ["role", ClaimTypes.Role];

    /// <summary>Claim types that carry the subject.</summary>
    private static readonly string[] SubjectClaimTypes = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>The claim type of the OAuth client of the token.</summary>
    private const string ClientIdClaimType = "client_id";

    /// <summary>
    ///     Maps a validated <see cref="ClaimsPrincipal" /> (the user of an HTTP request after authentication). The
    ///     <c>scope</c> claim may hold one space-separated string or several values. The <c>client_id</c> claim is mapped
    ///     to <see cref="ClientId" /> only when the token has no subject (client credentials, AB#8078); a user token keeps
    ///     <see cref="ClientId" /> <c>null</c>, so its <see cref="Subject" /> is always its <c>sub</c>.
    /// </summary>
    /// <param name="principal">An authenticated principal. The method does not validate tokens.</param>
    public static CkMethodPrincipal FromClaimsPrincipal(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = SubjectClaimTypes.Select(principal.FindFirst).Select(c => c?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var clientId = subject == null ? principal.FindFirst(ClientIdClaimType)?.Value : null;
        var roles = RoleClaimTypes.SelectMany(principal.FindAll).Select(c => c.Value)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var scopes = principal.FindAll(Infrastructure.InfrastructureCommon.ClaimScope)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new CkMethodPrincipal(subject, roles, scopes,
            string.IsNullOrWhiteSpace(clientId) ? null : clientId);
    }
}
