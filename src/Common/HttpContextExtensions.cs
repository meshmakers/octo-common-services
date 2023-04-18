using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.Common;

/// <summary>
/// Helper functions for http contexts
/// </summary>
public static class HttpContextExtensions
{
    /// <summary>
    /// Get the bearer access token if exists
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
}
