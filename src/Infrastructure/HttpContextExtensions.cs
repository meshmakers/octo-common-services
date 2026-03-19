namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
///     Helper functions for http contexts
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    ///     Get the bearer access token if exists
    /// </summary>
    /// <param name="httpContext"></param>
    /// <param name="bearerToken"></param>
    /// <returns></returns>
    public static bool TryGetBearerAccessToken(this HttpContext httpContext, out string? bearerToken)
    {
        bearerToken = null;
        string? authHeader = httpContext.Request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return false;
        }

        if (!authHeader.ToLower().StartsWith("bearer"))
        {
            return false;
        }

        bearerToken = authHeader.Substring("Bearer ".Length).Trim();
        return true;
    }

    /// <summary>
    ///     Returns the route value of the tenant id
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static string? GetTenantId(this HttpContext httpContext)
    {
        var tenantId = (string?)httpContext.GetRouteValue(InfrastructureCommon.TenantIdRoute);

        return tenantId;
    }

    /// <summary>
    ///     Extracts the tenant id from the URL path.
    ///     Supports two route patterns:
    ///     - /{tenantId}/... (e.g. /octosystem/_configuration)
    ///     - /tenants/{tenantId}/... (e.g. /tenants/octosystem/GraphQL)
    ///     This is used by CORS middleware which runs before routing is resolved.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns>The tenant id or null if the path has no segments</returns>
    public static string? GetTenantIdFromPath(this HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        // Pattern: /tenants/{tenantId}/...
        if (segments[0].Equals("tenants", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
        {
            return segments[1];
        }

        // Pattern: /{tenantId}/...
        return segments[0];
    }
}