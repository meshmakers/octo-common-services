using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Middleware that validates the route tenant against the caller's <c>tenant_id</c> claim.
///     The token must have been issued for the specific tenant being accessed.
///     Must be placed after UseAuthentication() and UseAuthorization() in the pipeline.
/// </summary>
/// <remarks>
///     <para>
///         <b>Service tokens (AB#5032).</b> A client-credentials token carries no <c>sub</c> claim.
///         Such a token used to skip the tenant check entirely — and because the JWT options run with
///         <c>ValidateAudience = false</c>, that made <b>every</b> client-credentials client of the
///         authority able to address <b>every</b> tenant, not just the two platform components that
///         actually need it. The check is therefore narrowed in two steps, governed by
///         <see cref="TenantAuthorizationOptions.ServiceTokenEnforcement" />: the default
///         <see cref="ServiceTokenTenantEnforcementMode.LogOnly" /> changes no request outcome but logs
///         every access that the enforcing mode would refuse — that log is the consumer inventory an
///         operator needs before switching an environment to
///         <see cref="ServiceTokenTenantEnforcementMode.Enforce" />.
///     </para>
///     <para>
///         The service-token path deliberately accepts only the <c>tenant_id</c> claim (plus the
///         explicit allow-list), never <c>allowed_tenants</c> — same stance as the user path below:
///         <c>allowed_tenants</c> is a tenant-<i>selection</i> hint, not an API authorization.
///     </para>
///     <para>
///         <b>User tokens (AB#5054).</b> 🔴 Everything in this class only ever runs on a principal
///         whose <c>Identity.AuthenticationType</c> reads <c>"Bearer"</c> — the guard a few lines
///         down that keeps cookie principals out. That label is not the scheme name; it comes from
///         <c>TokenValidationParameters.AuthenticationType</c>, which the JWT bearer handler leaves
///         at the framework default <c>"AuthenticationTypes.Federation"</c> unless the host sets it.
///         A host that does not set it runs this whole middleware as a no-op on every bearer
///         request — the user check included, and the service-token audit log with it, which is why
///         the inventory of several services stayed empty. Setting the label therefore switches the
///         user path from "never checked" to "always refused" in a single step, so it is staged the
///         same way, behind <see cref="TenantAuthorizationOptions.UserTokenEnforcement" /> — but
///         defaulting to <see cref="UserTokenTenantEnforcementMode.Enforce" />, so a host where the
///         check is genuinely live cannot be weakened by the option's mere existence.
///     </para>
///     <para>
///         <b>The parent-tenant administration rule (AB#5060).</b> On an endpoint marked
///         <see cref="IAllowParentTenantAdministration" /> — and <b>only</b> there — a user token may
///         also address a tenant <i>below</i> its own, because a parent administrator administers
///         their child tenants (backup, restore, archive export). A parent administrator explicitly
///         does <b>not</b> get access to the child's <i>data</i>, so the rule is opt-in at the
///         operation instead of a mode of this gate: an unmarked endpoint keeps the exact match, and
///         no GraphQL, entity or query route is ever to be marked. It applies to
///         <b>user tokens only</b>: a client-credentials token's <c>tenant_id</c> proves nothing —
///         mirrored clients share the parent's secret, and a token minted without <c>acr_values</c>
///         falls back to the <i>system</i> tenant, which is the root of the hierarchy, so the same
///         rule on that path would hand every service client of the authority every tenant route in
///         the estate (AB#5058, AB#5061, AB#5065). Service tokens keep the exact match plus their
///         own allow-list.
///     </para>
/// </remarks>
internal class TenantAuthorizationMiddleware(
    RequestDelegate next,
    IOptions<TenantAuthorizationOptions> options,
    ILogger<TenantAuthorizationMiddleware> logger)
{
    private const string TenantIdClaimType = "tenant_id";
    private const string ClientIdClaimType = "client_id";

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip endpoints marked with [AllowAnonymous] — they don't require tenant validation.
        // This is important because CookieBasedAuthenticationMiddleware may inject a Bearer header
        // from the OctoIdentityAccessToken cookie, causing false positives on anonymous endpoints.
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            await next(context);
            return;
        }

        // Only validate for bearer token authentication.
        // Cookie-authenticated requests (e.g., Identity Service SPA) are already
        // scoped per tenant via TenantCookieManager and do not carry tenant_id claims.
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Even if a Bearer header is present, skip tenant validation when the user was
        // actually authenticated via cookies. CookieBasedAuthenticationMiddleware may inject
        // a Bearer header from the OctoIdentityAccessToken cookie, but the default auth
        // scheme resolves to Identity.Application — a cookie principal that lacks tenant_id
        // claims. Validating that principal against the Bearer path causes false 403 errors.
        if (context.User.Identity is { IsAuthenticated: true, AuthenticationType: { } authType } &&
            !authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Skip for unauthenticated requests (let auth middleware handle 401)
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Get the route tenant ID. A route without a tenant segment addresses no tenant, so there is
        // nothing to validate — for user and service tokens alike.
        var tenantId = context.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            await next(context);
            return;
        }

        // A client-credentials token has no user, i.e. no "sub" claim.
        // Check both "sub" (unmapped) and ClaimTypes.NameIdentifier (mapped) because
        // JWT Bearer middleware may map "sub" to NameIdentifier when MapInboundClaims is
        // true (the default). Without this, user tokens are misidentified as client-credentials
        // and the entire tenant check is bypassed.
        var isServiceToken = !context.User.HasClaim(c =>
            c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
        if (isServiceToken)
        {
            if (await AllowServiceTokenAsync(context, tenantId))
            {
                await next(context);
            }

            return;
        }

        // The token must have been issued for the specific tenant being accessed.
        // The tenant_id claim identifies the tenant the user authenticated against.
        // allowed_tenants is only used for tenant selection (e.g., tenant picker UI),
        // not for authorizing API access.
        if (!await AllowUserTokenAsync(context, endpoint, tenantId))
        {
            return;
        }

        await next(context);
    }

    /// <summary>
    ///     Decides whether a user token may address <paramref name="routeTenantId" />. Returns
    ///     <c>true</c> to continue the pipeline; when it returns <c>false</c> the response has
    ///     already been set to <c>403 Forbidden</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Staged behind <see cref="TenantAuthorizationOptions.UserTokenEnforcement" /> since
    ///         AB#5054, for the same reason the service path is staged — with the opposite default.
    ///         See <see cref="UserTokenTenantEnforcementMode" /> for why.
    ///     </para>
    ///     <para>
    ///         <b>Parent-tenant rule (AB#5060), opt-in per endpoint.</b> On an endpoint marked
    ///         <see cref="IAllowParentTenantAdministration" /> a user token may also address a tenant
    ///         <i>below</i> its own, because administering a child tenant (backup, restore, archive
    ///         export) is a parent administrator's job. On every other endpoint nothing changes —
    ///         the parent explicitly does <b>not</b> get the child's data routes, which is why this
    ///         is a marker at the operation and not a mode of the gate.
    ///     </para>
    ///     <para>
    ///         The equality case is the common one and is answered first, before the metadata lookup
    ///         and before any hierarchy resolution. The rule is evaluated <b>before</b> the
    ///         enforcement branch on purpose: in <see cref="UserTokenTenantEnforcementMode.LogOnly" />
    ///         an access that the rule grants must not appear in the inventory as "would be denied",
    ///         because it would not be.
    ///     </para>
    /// </remarks>
    private async Task<bool> AllowUserTokenAsync(HttpContext context, Endpoint? endpoint, string routeTenantId)
    {
        var tokenTenantId = context.User.FindFirstValue(TenantIdClaimType);
        var matches = !string.IsNullOrEmpty(tokenTenantId) &&
                      string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(tokenTenantId) &&
            endpoint?.Metadata.GetMetadata<IAllowParentTenantAdministration>() != null &&
            await IsChildTenantAsync(context, tokenTenantId, routeTenantId))
        {
            // Logged on every grant, at Information: this rule widens access, so the only thing an
            // operator can be asked to review afterwards is who actually uses it. Administering a
            // child tenant is rare — if this line ever becomes hot in a host, that is the finding,
            // not noise to be suppressed.
            logger.LogInformation(
                "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' " +
                "and administers its child tenant '{RouteTenantId}' on endpoint '{Endpoint}'; allowed by the " +
                "parent-tenant administration rule (AB#5060)",
                context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClientIdClaimType) ?? "<none>",
                tokenTenantId,
                routeTenantId,
                endpoint.DisplayName ?? "<unnamed>");
            return true;
        }

        if (options.Value.UserTokenEnforcement == UserTokenTenantEnforcementMode.Enforce)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        // LogOnly: the inventory. The client id is what makes an entry actionable — it names the
        // application (Studio, the accounting app, the CLI, …), which is the unit an operator can
        // actually go and fix; the subject alone only says that *someone* did it.
        logger.LogWarning(
            "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' " +
            "but addresses tenant '{RouteTenantId}'. This would be denied with " +
            "UserTokenEnforcement=Enforce (AB#5054)",
            context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            context.User.FindFirstValue(ClientIdClaimType) ?? "<none>",
            string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId,
            routeTenantId);
        return true;
    }

    /// <summary>
    ///     Whether <paramref name="routeTenantId" /> is a child of <paramref name="tokenTenantId" />,
    ///     answered by the cached <see cref="ITenantHierarchyReader" /> (AB#5060).
    /// </summary>
    /// <remarks>
    ///     The reader is resolved per request rather than injected into the constructor: this
    ///     middleware is constructed once from the root provider, and a host that does not register
    ///     the reader (or the <c>ISystemContext</c> it needs) must not fail at startup — it simply
    ///     runs without the parent-tenant rule, i.e. on the exact match this class had before.
    ///     Missing means <c>false</c>, so the fallback is the closed one.
    /// </remarks>
    private async Task<bool> IsChildTenantAsync(HttpContext context, string tokenTenantId, string routeTenantId)
    {
        var services = context.RequestServices;
        var reader = services?.GetService<ITenantHierarchyReader>();
        if (reader == null)
        {
            logger.LogWarning(
                "No {Reader} is registered, so the parent-tenant administration rule cannot be applied to " +
                "tenant '{RouteTenantId}' and the request is judged by the exact tenant match (AB#5060)",
                nameof(ITenantHierarchyReader), routeTenantId);
            return false;
        }

        return await reader.IsChildTenantAsync(tokenTenantId, routeTenantId);
    }

    /// <summary>
    ///     Decides whether a client-credentials token may address <paramref name="routeTenantId" />.
    ///     Returns <c>true</c> to continue the pipeline; when it returns <c>false</c> the response has
    ///     already been set to <c>403 Forbidden</c>.
    /// </summary>
    private Task<bool> AllowServiceTokenAsync(HttpContext context, string routeTenantId)
    {
        var settings = options.Value;
        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Disabled)
        {
            return Task.FromResult(true);
        }

        var clientId = context.User.FindFirstValue(ClientIdClaimType);

        // Explicitly allowed to work across tenants — the multi-tenant platform workers.
        if (settings.IsCrossTenantServiceClient(clientId))
        {
            logger.LogDebug(
                "Service token of client '{ClientId}' is allow-listed for cross-tenant access; tenant '{RouteTenantId}' not checked",
                clientId, routeTenantId);
            return Task.FromResult(true);
        }

        var tokenTenantId = context.User.FindFirstValue(TenantIdClaimType);
        if (string.IsNullOrEmpty(tokenTenantId))
        {
            // A token issued before the identity service started stamping tenant_id on
            // client-credentials tokens, or one minted without a tenant context. It cannot be
            // attributed to a tenant, so enforcing means refusing it.
            if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Enforce)
            {
                logger.LogWarning(
                    "Denied: service token of client '{ClientId}' carries no tenant_id claim but addresses tenant '{RouteTenantId}' (AB#5032)",
                    clientId, routeTenantId);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.FromResult(false);
            }

            logger.LogWarning(
                "Service token of client '{ClientId}' carries no tenant_id claim and addresses tenant '{RouteTenantId}'. " +
                "This would be denied with ServiceTokenEnforcement=Enforce (AB#5032)",
                clientId, routeTenantId);
            return Task.FromResult(true);
        }

        if (string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Enforce)
        {
            logger.LogWarning(
                "Denied: service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but addresses tenant '{RouteTenantId}' (AB#5032)",
                clientId, tokenTenantId, routeTenantId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.FromResult(false);
        }

        logger.LogWarning(
            "Service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but addresses tenant '{RouteTenantId}'. " +
            "This would be denied with ServiceTokenEnforcement=Enforce (AB#5032)",
            clientId, tokenTenantId, routeTenantId);
        return Task.FromResult(true);
    }
}
