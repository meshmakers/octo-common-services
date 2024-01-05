namespace Meshmakers.Octo.Services.Common;

/// <summary>
///     Backend common constants
/// </summary>
public static class BackendCommon
{
    /// <summary>
    ///     Authentication scheme name used for OIDC
    /// </summary>
    public const string OidcAuthenticationScheme = "oidc";

    /// <summary>
    ///     Scopes
    /// </summary>
    public const string ClaimScope = "scope";

    /// <summary>
    ///     Returns the tenant id route name
    /// </summary>
    public const string TenantIdRoute = "tenantId";

    /// <summary>
    ///     Returns the tenant repository name
    /// </summary>
    public const string TenantRepositoryName = "tenantRepository";
}