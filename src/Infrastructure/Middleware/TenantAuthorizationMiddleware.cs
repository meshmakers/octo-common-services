namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Middleware that validates the route tenant against the user's allowed_tenants claims.
///     Must be placed after UseAuthentication() and UseAuthorization() in the pipeline.
/// </summary>
internal class TenantAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only validate for bearer token authentication.
        // Cookie-authenticated requests (e.g., Identity Service SPA) are already
        // scoped per tenant via TenantCookieManager and do not carry allowed_tenants claims.
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

        // Skip for client-credentials-only tokens (no "sub" claim = no user)
        if (!context.User.HasClaim(c => c.Type == "sub"))
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

        // Check if the user has allowed_tenants claims
        var allowedTenants = context.User.FindAll("allowed_tenants")
            .Select(c => c.Value).ToList();

        // If no allowed_tenants claims (old tokens before this feature), deny access to be safe
        if (allowedTenants.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Validate the route tenant is in the allowed list (case-insensitive)
        if (!allowedTenants.Any(t => string.Equals(t, tenantId, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }
}
