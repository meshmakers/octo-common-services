using Meshmakers.Octo.Services.Infrastructure.Configuration;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>The verdict of <see cref="CkMethodCallerTenantRule" />.</summary>
internal enum CkMethodCallerTenantVerdict
{
    /// <summary>The caller may act in the tenant of the call.</summary>
    Allowed,

    /// <summary>A user token (with <c>sub</c>) issued for another tenant, or without <c>tenant_id</c>.</summary>
    UserTenantMismatch,

    /// <summary>A service token (no <c>sub</c>) of another tenant whose client is not on the cross-tenant allow-list.</summary>
    ServiceTenantMismatch,

    /// <summary>The token has neither <c>sub</c> nor <c>client_id</c>: it cannot be attributed to a caller.</summary>
    NoIdentity
}

/// <summary>
///     The one caller-tenant rule of CK method calls (AB#8711), shared by the bus host (<see cref="CkMethodCallerTokenValidator" />)
///     and the in-process transport (<see cref="CkMethodCallerTenantFilter" />). It is the rule of
///     <c>TenantAuthorizationMiddleware</c>, always enforcing: a user token (with <c>sub</c>) must carry the call's tenant in
///     <c>tenant_id</c>; a service token (client credentials, no <c>sub</c>) needs a <c>client_id</c> and must carry the call's
///     tenant too or be listed in <c>TenantAuthorization:CrossTenantServiceClientIds</c>. The tenant ids are compared
///     case-insensitively. <c>allowed_tenants</c> is never consulted, the <c>LogOnly</c>/<c>Disabled</c> modes of the HTTP side
///     never apply, and there is no parent-tenant rule.
/// </summary>
internal static class CkMethodCallerTenantRule
{
    /// <summary>Judges the token claims against the tenant of the call.</summary>
    /// <param name="subjectId">The <c>sub</c> claim.</param>
    /// <param name="clientId">The <c>client_id</c> claim.</param>
    /// <param name="tokenTenantId">The <c>tenant_id</c> claim.</param>
    /// <param name="callTenantId">The tenant of the call (route tenant, <c>CkMethodRequest.TenantId</c>).</param>
    /// <param name="tenantOptions">The cross-tenant allow-list.</param>
    public static CkMethodCallerTenantVerdict Evaluate(string? subjectId, string? clientId, string? tokenTenantId,
        string callTenantId, TenantAuthorizationOptions tenantOptions)
    {
        ArgumentNullException.ThrowIfNull(tenantOptions);

        var tenantMatches = !string.IsNullOrEmpty(tokenTenantId) &&
                            string.Equals(tokenTenantId, callTenantId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            return tenantMatches ? CkMethodCallerTenantVerdict.Allowed : CkMethodCallerTenantVerdict.UserTenantMismatch;
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return CkMethodCallerTenantVerdict.NoIdentity;
        }

        return tenantMatches || tenantOptions.IsCrossTenantServiceClient(clientId)
            ? CkMethodCallerTenantVerdict.Allowed
            : CkMethodCallerTenantVerdict.ServiceTenantMismatch;
    }
}
