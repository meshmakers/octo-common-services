using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>The verdict of <see cref="CkMethodCallerTenantRule" />.</summary>
internal enum CkMethodCallerTenantVerdict
{
    /// <summary>The caller may act in the tenant of the call.</summary>
    Allowed,

    /// <summary>
    ///     A user token of a parent tenant calls a method of <see cref="CkParentTenantAdministrationMethods" /> on a registered
    ///     child tenant: allowed by the parent-tenant administration rule (<see cref="CkMethodCallerTenantRule.EvaluateAsync" />).
    /// </summary>
    AllowedAsParentTenantAdministrator,

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
///     case-insensitively. <c>allowed_tenants</c> is never consulted and the <c>LogOnly</c>/<c>Disabled</c> modes of the HTTP side
///     never apply.
///     <para>
///         <b>Parent-tenant administration.</b> <see cref="EvaluateAsync" /> adds the one widening the HTTP side has on endpoints
///         marked <c>[AllowParentTenantAdministration]</c>, scoped to the closed method list
///         <see cref="CkParentTenantAdministrationMethods" />: a <b>user</b> token (with <c>sub</c>) issued for tenant P may call
///         a method of that list on tenant C when <c>ITenantHierarchyReader.IsChildTenantAsync(P, C)</c> is true (one level for an
///         ordinary parent, the whole estate for the system tenant; cached, fail-closed). Every other method, every service token
///         and every caller without a hierarchy reader keeps the exact match of <see cref="Evaluate" />. The grant is logged at
///         Information (subject, client, token tenant, call tenant, method id; never the token or the parameters). The tenant
///         generation step below checks the token tenant P on this path too.
///     </para>
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
    ///     <see cref="Evaluate" /> plus the parent-tenant administration rule: a user token that <see cref="Evaluate" /> refuses
    ///     with <see cref="CkMethodCallerTenantVerdict.UserTenantMismatch" /> is allowed
    ///     (<see cref="CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator" />) when <paramref name="methodId" /> is on
    ///     <see cref="CkParentTenantAdministrationMethods" />, the token carries a <c>tenant_id</c> and
    ///     <paramref name="hierarchyReader" /> answers that the call tenant is a child of it. Without a reader the rule is off
    ///     (fail-closed). The grant is logged at Information when <paramref name="logger" /> is given.
    /// </summary>
    /// <param name="subjectId">The <c>sub</c> claim.</param>
    /// <param name="clientId">The <c>client_id</c> claim.</param>
    /// <param name="tokenTenantId">The <c>tenant_id</c> claim.</param>
    /// <param name="callTenantId">The tenant of the call.</param>
    /// <param name="methodId">The qualified, version-less method id of the call.</param>
    /// <param name="tenantOptions">The cross-tenant allow-list.</param>
    /// <param name="hierarchyReader">The cached hierarchy reader; <c>null</c> = no parent-tenant rule.</param>
    /// <param name="logger">Logs the grant; <c>null</c> = not logged.</param>
    /// <param name="transport">Names the place of the check in the log (gateway, in-process, bus).</param>
    public static async ValueTask<CkMethodCallerTenantVerdict> EvaluateAsync(string? subjectId, string? clientId,
        string? tokenTenantId, string callTenantId, string? methodId, TenantAuthorizationOptions tenantOptions,
        ITenantHierarchyReader? hierarchyReader, ILogger? logger, string transport)
    {
        var verdict = Evaluate(subjectId, clientId, tokenTenantId, callTenantId, tenantOptions);
        if (verdict != CkMethodCallerTenantVerdict.UserTenantMismatch ||
            hierarchyReader is null ||
            string.IsNullOrEmpty(tokenTenantId) ||
            string.IsNullOrEmpty(callTenantId) ||
            !CkParentTenantAdministrationMethods.Contains(methodId))
        {
            return verdict;
        }

        bool isChild;
        try
        {
            isChild = await hierarchyReader.IsChildTenantAsync(tokenTenantId, callTenantId).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the reader is fail-closed by contract; a reader that throws anyway refuses, it never opens the rule
            logger?.LogWarning(
                "The tenant hierarchy could not be read for CK method {MethodId} in tenant {CallTenantId} ({ExceptionType}); the parent-tenant administration rule refuses",
                methodId, callTenantId, e.GetType().Name);
            return verdict;
        }

        if (!isChild)
        {
            return verdict;
        }

        // Logged on every grant: this rule widens access, so who uses it must be reviewable afterwards.
        logger?.LogInformation(
            "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' and calls CK method " +
            "{MethodId} on its child tenant '{CallTenantId}' ({Transport}); allowed by the parent-tenant administration rule",
            subjectId, string.IsNullOrWhiteSpace(clientId) ? "<none>" : clientId, tokenTenantId, methodId, callTenantId,
            transport);
        return CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator;
    }

    /// <summary><c>true</c> for <see cref="CkMethodCallerTenantVerdict.Allowed" /> and the parent-tenant grant.</summary>
    public static bool IsAllowed(this CkMethodCallerTenantVerdict verdict) =>
        verdict is CkMethodCallerTenantVerdict.Allowed or CkMethodCallerTenantVerdict.AllowedAsParentTenantAdministrator;

    /// <summary>
    ///     The tenant generation step (AB#8999) for a caller that <see cref="Evaluate" /> or <see cref="EvaluateAsync" /> allowed:
    ///     checks <c>tenant_gen</c> against the current generation of the token's own <c>tenant_id</c> (never the call tenant; on
    ///     the parent-tenant path that is the parent P) and returns the
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
