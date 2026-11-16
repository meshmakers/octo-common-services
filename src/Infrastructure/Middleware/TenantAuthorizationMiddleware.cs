using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
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
///     <para>
///         <b>Metric.</b> Every decision of the tenant check is counted in <c>octo.tenant_auth.decisions</c>
///         (<see cref="TenantAuthorizationMetrics" />) next to its log line, so the inventory of the <c>LogOnly</c>
///         modes can be read per service, client and route without searching log text.
///     </para>
/// </remarks>
internal class TenantAuthorizationMiddleware(
    RequestDelegate next,
    IOptions<TenantAuthorizationOptions> options,
    ILogger<TenantAuthorizationMiddleware> logger)
{
    private const string TenantIdClaimType = "tenant_id";
    private const string ClientIdClaimType = "client_id";
    private const bool ServiceToken = true;
    private const bool UserToken = false;

    private int _missingMetricsLogged;

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
        var tokenTenantId = context.User.FindFirstValue(TenantIdClaimType);
        var clientId = context.User.FindFirstValue(ClientIdClaimType);
        if (isServiceToken)
        {
            if (!await AllowServiceTokenAsync(context, endpoint, tenantId))
            {
                return;
            }

            // AB#8999: an allow-listed cross-tenant service client (route != tenant_id) keeps today's routing rule;
            // its token tenant (normally the system tenant) is not checked.
            var isCrossTenantClient =
                !string.Equals(tokenTenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                options.Value.IsCrossTenantServiceClient(clientId);
            if (!isCrossTenantClient && !await AllowTenantGenerationAsync(context, tokenTenantId, clientId))
            {
                return;
            }

            await next(context);
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

        // AB#8999: checked against the token tenant, also on the parent-tenant path (AB#5060).
        if (!await AllowTenantGenerationAsync(context, tokenTenantId, clientId))
        {
            return;
        }

        await next(context);
    }

    /// <summary>
    ///     The tenant generation check (AB#8999, security item AB#8970): refuses, in <c>Enforce</c>, a token issued for an
    ///     earlier tenant with the same id (<c>tenant_gen</c> missing or different). Runs after the tenant match, always
    ///     against the token's <c>tenant_id</c>. Returns <c>true</c> to continue; when it returns <c>false</c> the response
    ///     is already <c>403</c> (mismatch / missing) or <c>503</c> (lookup failed).
    /// </summary>
    /// <remarks>
    ///     The check is resolved per request like <see cref="ITenantHierarchyReader" />: a host without the registration
    ///     (<c>AddOctoServiceInfrastructure</c> / <c>AddOctoTenantGenerationCheck</c>) runs without it.
    /// </remarks>
    private async Task<bool> AllowTenantGenerationAsync(HttpContext context, string? tokenTenantId, string? clientId)
    {
        var check = context.RequestServices?.GetService<ITenantGenerationCheck>();
        if (check == null)
        {
            return true;
        }

        var result = await check.CheckAsync(new TenantGenerationCheckRequest(tokenTenantId,
            TenantGenerationClaim.FromPrincipal(context.User), TenantGenerationTransports.Http, clientId),
            context.RequestAborted);
        switch (result.Decision)
        {
            case TenantGenerationDecision.Deny:
                await WriteForbiddenAsync(context,
                    $"Access is denied: the token was issued for an earlier tenant '{tokenTenantId}' that was deleted " +
                    "and created again, or before the tenant generation was issued. Sign in again.");
                return false;
            case TenantGenerationDecision.Unavailable:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                if (!context.Response.HasStarted)
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "The tenant of the token cannot be verified right now. Try again later."
                    });
                }

                return false;
            default:
                return true;
        }
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
    ///         export) is a parent administrator's job. An endpoint that also serves tenant content (a
    ///         GraphQL endpoint) instead carries <see cref="IAllowParentTenantAdministrationRequest" />
    ///         (AB#8986): the rule then applies only to a request the predicate classifies as exclusively
    ///         administration; every other request on that endpoint keeps the exact match. On every other endpoint nothing changes —
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
        var clientId = context.User.FindFirstValue(ClientIdClaimType);
        var matches = !string.IsNullOrEmpty(tokenTenantId) &&
                      string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            Count(context, UserToken, clientId, endpoint, TenantAuthorizationOutcome.Match);
            return true;
        }

        // The endpoint-wide marker, or the per-request predicate of an endpoint that also serves tenant content (a GraphQL
        // endpoint): the predicate is asked only for a user token of another tenant, and only when the endpoint carries no
        // marker. It fails closed (false, also when it throws).
        var isParentAdministrationEndpoint =
            endpoint?.Metadata.GetMetadata<IAllowParentTenantAdministration>() != null;
        var isParentAdministrationRequest = !isParentAdministrationEndpoint &&
                                            !string.IsNullOrEmpty(tokenTenantId) &&
                                            await IsParentAdministrationRequestAsync(context, endpoint);
        if (!string.IsNullOrEmpty(tokenTenantId) &&
            (isParentAdministrationEndpoint || isParentAdministrationRequest) &&
            await IsChildTenantAsync(context, tokenTenantId, routeTenantId))
        {
            // Logged on every grant, at Information: this rule widens access, so the only thing an
            // operator can be asked to review afterwards is who actually uses it. Administering a
            // child tenant is rare — if this line ever becomes hot in a host, that is the finding,
            // not noise to be suppressed.
            logger.LogInformation(
                "User token of subject '{Subject}' (client '{ClientId}') was issued for tenant '{TokenTenantId}' " +
                "and administers its child tenant '{RouteTenantId}' on endpoint '{Endpoint}' ({Basis}); allowed by the " +
                "parent-tenant administration rule (AB#5060)",
                context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClientIdClaimType) ?? "<none>",
                tokenTenantId,
                routeTenantId,
                endpoint?.DisplayName ?? "<unnamed>",
                isParentAdministrationEndpoint ? "endpoint marker" : "request predicate");
            Count(context, UserToken, clientId, endpoint, TenantAuthorizationOutcome.AllowedParentAdmin);
            return true;
        }

        if (options.Value.UserTokenEnforcement == UserTokenTenantEnforcementMode.Enforce)
        {
            // The denial names its reason in the log AND in the response body (AB#5227): a bare 403
            // reads as "no permission" to the operator, while the actual cause here is often that the
            // route tenant does not exist (any more) — the parent-tenant rule only grants access to an
            // EXISTING child, so restoring into a deleted tenant fails on this exact branch. One
            // combined message covers "not a child" and "does not exist" without telling a caller who
            // is neither which of the two it was.
            // the combined "not an existing child" message also for an administration request of a request predicate
            var isParentAdministration = isParentAdministrationEndpoint || isParentAdministrationRequest;
            logger.LogWarning(
                "Denied: user token of subject '{Subject}' (client '{ClientId}') was issued for tenant " +
                "'{TokenTenantId}' but addresses tenant '{RouteTenantId}' (parent administration endpoint or request: " +
                "{IsParentAdministration}) (AB#5054)",
                context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                context.User.FindFirstValue(ClientIdClaimType) ?? "<none>",
                string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId,
                routeTenantId,
                isParentAdministration);
            Count(context, UserToken, clientId, endpoint, string.IsNullOrEmpty(tokenTenantId)
                ? TenantAuthorizationOutcome.DeniedMissingTenantId
                : TenantAuthorizationOutcome.Denied);
            var message = isParentAdministration
                ? $"Access to tenant '{routeTenantId}' is denied: the token was issued for tenant " +
                  $"'{tokenTenantId}', and '{routeTenantId}' is not an existing child tenant of it. " +
                  "If the tenant was deleted, re-create it first; a tenant created moments ago may " +
                  "take up to a minute to be recognized."
                : $"Access to tenant '{routeTenantId}' is denied: the token was issued for tenant " +
                  $"'{tokenTenantId}'. Sign in to tenant '{routeTenantId}' to access it.";
            await WriteForbiddenAsync(context, message);
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
        Count(context, UserToken, clientId, endpoint, string.IsNullOrEmpty(tokenTenantId)
            ? TenantAuthorizationOutcome.WouldDenyMissingTenantId
            : TenantAuthorizationOutcome.WouldDeny);
        return true;
    }

    /// <summary>
    ///     Asks the endpoint's <see cref="IAllowParentTenantAdministrationRequest" />, if any, whether the request consists
    ///     exclusively of parent-tenant administration operations. No predicate, or one that throws, is <c>false</c>.
    /// </summary>
    private async Task<bool> IsParentAdministrationRequestAsync(HttpContext context, Endpoint? endpoint)
    {
        var predicate = endpoint?.Metadata.GetMetadata<IAllowParentTenantAdministrationRequest>();
        if (predicate == null)
        {
            return false;
        }

        // The predicate may read the body; buffer it here and rewind afterwards, so the endpoint always gets the whole
        // body (also after a predicate that did not rewind or failed half-way), independent of the predicate's contract.
        context.Request.EnableBuffering();
        try
        {
            return await predicate.IsParentTenantAdministrationRequestAsync(context);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(
                "The parent-tenant administration request predicate of endpoint '{Endpoint}' failed ({ExceptionType}); the request is judged by the exact tenant match",
                endpoint?.DisplayName ?? "<unnamed>", e.GetType().Name);
            return false;
        }
        finally
        {
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }
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
    private async Task<bool> AllowServiceTokenAsync(HttpContext context, Endpoint? endpoint, string routeTenantId)
    {
        var settings = options.Value;
        var clientId = context.User.FindFirstValue(ClientIdClaimType);
        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Disabled)
        {
            Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.NotChecked);
            return true;
        }

        // Explicitly allowed to work across tenants — the multi-tenant platform workers.
        if (settings.IsCrossTenantServiceClient(clientId))
        {
            logger.LogDebug(
                "Service token of client '{ClientId}' is allow-listed for cross-tenant access; tenant '{RouteTenantId}' not checked",
                clientId, routeTenantId);
            // On its own tenant the allow-list grants nothing extra: counted as a match, like InvokeAsync treats it.
            var ownTenant = string.Equals(context.User.FindFirstValue(TenantIdClaimType), routeTenantId,
                StringComparison.OrdinalIgnoreCase);
            Count(context, ServiceToken, clientId, endpoint, ownTenant
                ? TenantAuthorizationOutcome.Match
                : TenantAuthorizationOutcome.AllowedCrossTenantClient);
            return true;
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
                Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.DeniedMissingTenantId);
                await WriteForbiddenAsync(context,
                    $"Access to tenant '{routeTenantId}' is denied: the service token carries no tenant. " +
                    $"Request the token with acr_values=tenant:{routeTenantId}.");
                return false;
            }

            logger.LogWarning(
                "Service token of client '{ClientId}' carries no tenant_id claim and addresses tenant '{RouteTenantId}'. " +
                "This would be denied with ServiceTokenEnforcement=Enforce (AB#5032)",
                clientId, routeTenantId);
            Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.WouldDenyMissingTenantId);
            return true;
        }

        if (string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.Match);
            return true;
        }

        if (settings.ServiceTokenEnforcement == ServiceTokenTenantEnforcementMode.Enforce)
        {
            logger.LogWarning(
                "Denied: service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but addresses tenant '{RouteTenantId}' (AB#5032)",
                clientId, tokenTenantId, routeTenantId);
            Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.Denied);
            await WriteForbiddenAsync(context,
                $"Access to tenant '{routeTenantId}' is denied: the service token was issued for tenant " +
                $"'{tokenTenantId}'. Request the token with acr_values=tenant:{routeTenantId}.");
            return false;
        }

        logger.LogWarning(
            "Service token of client '{ClientId}' was issued for tenant '{TokenTenantId}' but addresses tenant '{RouteTenantId}'. " +
            "This would be denied with ServiceTokenEnforcement=Enforce (AB#5032)",
            clientId, tokenTenantId, routeTenantId);
        Count(context, ServiceToken, clientId, endpoint, TenantAuthorizationOutcome.WouldDeny);
        return true;
    }

    /// <summary>
    ///     Counts a decision in <c>octo.tenant_auth.decisions</c>. The metrics are resolved per request like
    ///     <see cref="ITenantHierarchyReader" />: a host without the registration runs without the counter and logs that
    ///     once, because a missing counter must not read as a clean inventory.
    /// </summary>
    private void Count(HttpContext context, bool serviceToken, string? clientId, Endpoint? endpoint,
        TenantAuthorizationOutcome outcome)
    {
        var services = context.RequestServices;
        var metrics = services?.GetService<TenantAuthorizationMetrics>();
        if (metrics == null)
        {
            // A request without a service provider exists only in unit tests.
            if (services != null && Interlocked.Exchange(ref _missingMetricsLogged, 1) == 0)
            {
                logger.LogWarning(
                    "No {Metrics} is registered, so the decisions of the tenant check are not counted in " +
                    "{Counter}; register it with AddOctoTenantAuthorization()",
                    nameof(TenantAuthorizationMetrics), TenantAuthorizationMetrics.CounterName);
            }

            return;
        }

        if (serviceToken)
        {
            metrics.RecordServiceToken(outcome, options.Value.ServiceTokenEnforcement, clientId, endpoint);
        }
        else
        {
            metrics.RecordUserToken(outcome, options.Value.UserTokenEnforcement, clientId, endpoint);
        }
    }

    /// <summary>
    ///     Sets <c>403 Forbidden</c> and writes a JSON body naming the reason (AB#5227). The shape
    ///     (<c>{"message": ...}</c>) matches what the frontends' HTTP error interceptor renders, so
    ///     the reason reaches the operator instead of a generic "access denied" toast. Written
    ///     best-effort: a response that has already started keeps the bare status code.
    /// </summary>
    private static async Task WriteForbiddenAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        if (!context.Response.HasStarted)
        {
            await context.Response.WriteAsJsonAsync(new { message });
        }
    }
}
