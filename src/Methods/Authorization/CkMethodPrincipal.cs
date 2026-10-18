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
public sealed record CkMethodPrincipal(
    string? SubjectId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes)
{
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
