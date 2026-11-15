using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

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
///     <para>
///         <b>Tenant generation (AB#8999).</b> <see cref="CheckGenerationAsync" /> is the second step of the rule, run by both
///         transports after <see cref="Evaluate" /> allowed the call: the token's <c>tenant_gen</c> must name the current
///         generation of its own <c>tenant_id</c>. Unlike the tenant match it follows the mode
///         <c>TenantAuthorization:TenantGenerationEnforcement</c> (default <c>LogOnly</c>), so one value per environment
///         switches HTTP, methods and identity's grants together.
///     </para>
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

    /// <summary>
    ///     The tenant generation step (AB#8999) for a caller that <see cref="Evaluate" /> allowed: checks <c>tenant_gen</c>
    ///     against the current generation of the token's own <c>tenant_id</c> (never the call tenant) and returns the
    ///     decision after the mode. An allow-listed cross-tenant service client calling another tenant than its own is not
    ///     checked (today's routing rule; its token tenant is normally the system tenant). Transport label <c>method</c>.
    /// </summary>
    public static async ValueTask<TenantGenerationDecision> CheckGenerationAsync(ITenantGenerationCheck check,
        TenantAuthorizationOptions tenantOptions, string? subjectId, string? clientId, string? tokenTenantId,
        TenantGenerationClaim? generation, string callTenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(tenantOptions);

        var isCrossTenantClient = string.IsNullOrWhiteSpace(subjectId) &&
                                  !string.Equals(tokenTenantId, callTenantId, StringComparison.OrdinalIgnoreCase) &&
                                  tenantOptions.IsCrossTenantServiceClient(clientId);
        if (isCrossTenantClient)
        {
            return TenantGenerationDecision.Allow;
        }

        var result = await check.CheckAsync(new TenantGenerationCheckRequest(tokenTenantId, generation,
            TenantGenerationTransports.Method, clientId), cancellationToken).ConfigureAwait(false);
        return result.Decision;
    }
}
