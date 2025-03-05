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
    ///     Returns the tenant repository name used as a key in the http context items
    /// </summary>
    public const string TenantRepositoryName = "tenantRepository";

    /// <summary>
    /// Returns the tenant id name used as a key in the http context items
    /// </summary>
    public const string TenantIdName = "tenantId";
}