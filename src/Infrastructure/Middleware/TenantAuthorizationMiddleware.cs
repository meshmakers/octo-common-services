using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Middleware that validates the route tenant against the user's tenant_id claim.
///     The token must have been issued for the specific tenant being accessed.
///     Must be placed after UseAuthentication() and UseAuthorization() in the pipeline.
/// </summary>
internal class TenantAuthorizationMiddleware(RequestDelegate next)
{
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

        // Skip for unauthenticated requests (let auth middleware handle 401)
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Skip for client-credentials-only tokens (no "sub" claim = no user).
        // Check both "sub" (unmapped) and ClaimTypes.NameIdentifier (mapped) because
        // JWT Bearer middleware may map "sub" to NameIdentifier when MapInboundClaims is
        // true (the default). Without this, user tokens are misidentified as client-credentials
        // and the entire tenant check is bypassed.
        if (!context.User.HasClaim(c =>
                c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier))
        {
            await next(context);
            return;
        }

        // Get the route tenant ID
        var tenantId = context.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            await next(context);
            return;
        }

        // The token must have been issued for the specific tenant being accessed.
        // The tenant_id claim identifies the tenant the user authenticated against.
        // allowed_tenants is only used for tenant selection (e.g., tenant picker UI),
        // not for authorizing API access.
        var tokenTenantId = context.User.FindFirstValue("tenant_id");
        if (string.IsNullOrEmpty(tokenTenantId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!string.Equals(tokenTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }
}
