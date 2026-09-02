namespace Meshmakers.Octo.Services.Infrastructure.Configuration;

/// <summary>
///     How the tenant check of <c>TenantAuthorizationMiddleware</c> treats <b>service tokens</b>
///     (client-credentials tokens, i.e. tokens without a <c>sub</c> claim).
/// </summary>
/// <remarks>
///     Before AB#5032 a service token skipped the tenant check entirely. Together with
///     <c>ValidateAudience = false</c> in the JWT options that meant every client-credentials client of
///     the authority passed the transport check for <b>every</b> tenant. Narrowing that is a breaking
///     change for whoever relies on it, so it is rolled out in two steps: first observe
///     (<see cref="LogOnly" />, the default — behaviour identical to before, but every foreign-tenant
///     access is logged), then enforce (<see cref="Enforce" />) once the log has been evaluated.
/// </remarks>
public enum ServiceTokenTenantEnforcementMode
{
    /// <summary>
    ///     Pre-AB#5032 behaviour with no logging at all: a service token is never checked against the
    ///     route tenant. Only useful to silence the audit log in an environment that is known to be
    ///     mixed and cannot be cleaned up yet.
    /// </summary>
    Disabled = 0,

    /// <summary>
    ///     <b>Default.</b> Behaviourally identical to <see cref="Disabled" /> — every request is let
    ///     through — but a service token that addresses a tenant it was not issued for is logged as a
    ///     warning naming the client id, the route tenant and the token tenant. This is the consumer
    ///     inventory an operator needs before switching to <see cref="Enforce" />.
    /// </summary>
    LogOnly = 1,

    /// <summary>
    ///     A service token may only address the tenant it was issued for (<c>tenant_id</c> claim), or a
    ///     tenant it is explicitly allowed to cross into via
    ///     <see cref="TenantAuthorizationOptions.CrossTenantServiceClientIds" />. Everything else is
    ///     refused with <c>403 Forbidden</c> — including a service token that carries no
    ///     <c>tenant_id</c> claim at all, because such a token cannot be attributed to a tenant
    ///     (fail closed).
    /// </summary>
    Enforce = 2
}

/// <summary>
///     How the tenant check of <c>TenantAuthorizationMiddleware</c> treats <b>user tokens</b>
///     (tokens carrying a <c>sub</c> claim).
/// </summary>
/// <remarks>
///     <para>
///         The user path had no staging at all until AB#5054, because it was believed to have been
///         live everywhere since AB#5032. It was not: the middleware only looks at a principal whose
///         <c>Identity.AuthenticationType</c> reads <c>"Bearer"</c>, and that label comes from
///         <c>TokenValidationParameters.AuthenticationType</c>, which the JWT handler leaves at the
///         framework default <c>"AuthenticationTypes.Federation"</c> unless a host sets it. Only
///         octo-mcp-service ever did. So in identity, communication-controller, asset-repo, bot and
///         ai-services the check has never run on a single request, and switching the label on flips
///         the user path from "never checked" to "always refused" in one step — for the most-used
///         API on the platform.
///     </para>
///     <para>
///         Hence the same two-step rollout the service path already has: first observe
///         (<see cref="LogOnly" />), then enforce. The default is <see cref="Enforce" /> so that a
///         host where the check is genuinely live today (octo-mcp-service) is not weakened by this
///         option existing, and so that a host that never opts down is closed rather than open. The
///         zero value is the enforcing one on purpose: a default-constructed options object must
///         never be the permissive one.
///     </para>
/// </remarks>
public enum UserTokenTenantEnforcementMode
{
    /// <summary>
    ///     <b>Default.</b> A user token may only address the tenant it was issued for
    ///     (<c>tenant_id</c> claim). Everything else is refused with <c>403 Forbidden</c> —
    ///     including a user token that carries no <c>tenant_id</c> claim at all, which cannot be
    ///     attributed to a tenant (fail closed). <c>allowed_tenants</c> is deliberately not
    ///     consulted: it is a tenant-<i>selection</i> hint, not an API authorization.
    /// </summary>
    Enforce = 0,

    /// <summary>
    ///     Migration mode. Request outcomes are identical to a host whose gate never ran, but every
    ///     access an enforcing run would refuse is logged as a warning naming the subject, the
    ///     client id, the token tenant and the route tenant. That log is the consumer inventory an
    ///     operator needs before switching an environment to <see cref="Enforce" />.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no third "off" value. <see cref="LogOnly" /> already changes no
    ///     request outcome, so a silent mode would only buy the ability to hide the inventory.
    /// </remarks>
    LogOnly = 1
}

/// <summary>
///     Configuration of <c>TenantAuthorizationMiddleware</c> (AB#5032, AB#5054).
/// </summary>
/// <remarks>
///     Bind with <c>services.AddOctoTenantAuthorization(configuration)</c>. Without any registration the
///     defaults apply, which reproduce the behaviour before AB#5032 plus the audit log.
/// </remarks>
public class TenantAuthorizationOptions
{
    /// <summary>
    ///     Configuration section this class binds to (<c>OCTO_TENANTAUTHORIZATION__…</c>).
    /// </summary>
    public const string SectionName = "TenantAuthorization";

    /// <summary>
    ///     How service tokens (no <c>sub</c> claim) are treated. Defaults to
    ///     <see cref="ServiceTokenTenantEnforcementMode.LogOnly" />, i.e. the request behaviour of every
    ///     release before AB#5032.
    /// </summary>
    public ServiceTokenTenantEnforcementMode ServiceTokenEnforcement { get; set; } =
        ServiceTokenTenantEnforcementMode.LogOnly;

    /// <summary>
    ///     How user tokens (with a <c>sub</c> claim) are treated (AB#5054). Defaults to
    ///     <see cref="UserTokenTenantEnforcementMode.Enforce" />, i.e. the behaviour of every host
    ///     where the check actually runs today — this option can only ever be used to <i>loosen</i>
    ///     a specific host during its migration, never to silently leave one unprotected.
    /// </summary>
    public UserTokenTenantEnforcementMode UserTokenEnforcement { get; set; } =
        UserTokenTenantEnforcementMode.Enforce;

    /// <summary>
    ///     How long a resolved parent/child answer is reused before the hierarchy is read again
    ///     (AB#5060). Defaults to 60 seconds; <see cref="TimeSpan.Zero" /> disables caching.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Used by the parent-tenant rule: on an endpoint marked
    ///         <c>[AllowParentTenantAdministration]</c> a <b>user</b> token may also address a tenant
    ///         below its own. That check sits in the request path, so one hierarchy read per request
    ///         is not acceptable. 60 s is the same order as the data-permission resolver's TTL and
    ///         bounds the two windows that matter: a newly created child tenant is unreachable by its
    ///         parent for at most one TTL, and a tenant that was moved or deleted keeps being
    ///         reachable for at most one TTL. Negative answers are cached as well — the denial path is
    ///         the attacker-controllable one, so an uncached "no" would turn every 403 into a database
    ///         round trip.
    ///     </para>
    ///     <para>
    ///         There is deliberately <b>no</b> option that switches the parent-tenant rule on or off
    ///         globally. Its scope is the set of endpoints that carry the marker attribute, decided in
    ///         code where the operation is; a global flag could only either break exactly those
    ///         endpoints or — the dangerous direction — be turned into the blanket rule this was
    ///         designed not to be.
    ///     </para>
    /// </remarks>
    public TimeSpan TenantHierarchyCacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Client ids of service clients that genuinely fan out across tenants with a single token and
    ///     are therefore exempt from the tenant match. Matched case-insensitively against the token's
    ///     <c>client_id</c> claim.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Expected to stay empty.</b> The two consumers this exemption was believed to exist
    ///         for both mint <i>tenant-bound</i> tokens (<c>acr_values=tenant:{tenantId}</c>) and cache
    ///         them per tenant: the AI adapter worker (<c>octo-ai-services</c> <c>McpTokenIssuer</c>,
    ///         one token per session tenant, used against <c>/{tenantId}/mcp</c>) and the mesh adapter
    ///         (<c>ServiceAccountTokenService</c>, tenant from its own
    ///         <c>ServiceAccountConfiguration</c>). Once identity stamps <c>tenant_id</c> they pass the
    ///         match on their own. The <c>tenant_id</c> match is the mechanism; this list is only the
    ///         escape hatch.
    ///     </para>
    ///     <para>
    ///         Deliberately configuration, not a hard-coded list: the set differs per environment, and
    ///         a hard-coded list would have to be released to be changed. An entry may end in <c>*</c>
    ///         to match a prefix, for client families whose ids carry a generated suffix.
    ///     </para>
    ///     <para>
    ///         🔴 Never list the per-tenant pipeline service accounts here — they are provisioned one
    ///         per adapter <i>inside</i> one tenant and must stay bound to it. And never use this list
    ///         as a migration aid: an entry is a permanent hole, whereas
    ///         <see cref="ServiceTokenTenantEnforcementMode.LogOnly" /> is the migration mode.
    ///     </para>
    /// </remarks>
    public IList<string> CrossTenantServiceClientIds { get; set; } = new List<string>();

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="clientId" /> is listed in
    ///     <see cref="CrossTenantServiceClientIds" /> (exact, case-insensitive, or by a trailing
    ///     <c>*</c> prefix pattern).
    /// </summary>
    public bool IsCrossTenantServiceClient(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return false;
        }

        foreach (var entry in CrossTenantServiceClientIds)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var pattern = entry.Trim();
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (prefix.Length == 0 ||
                    clientId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(pattern, clientId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
