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
}