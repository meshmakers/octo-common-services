using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Common.Authorization;

/// <summary>
///     Allows to transform a jwt token to a cookie based auth
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CookieBasedAuthorizationMiddleware
{
    private const string CookieName = "OctoIdentityAccessToken";
    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerPrefix = "Bearer";
    private readonly RequestDelegate _next;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next"></param>
    public CookieBasedAuthorizationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    ///     Invokes the middleware
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request.Headers["Authorization"]))
            try
            {
                if (context.Request.Query.TryGetValue("jwt_token", out var tokenStringValues))
                    if (tokenStringValues.Count > 0)
                    {
                        context.Response.Cookies.Append(CookieName, tokenStringValues[0] ?? string.Empty);
                        context.Request.Headers.Add(AuthorizationHeaderName, new[] { $"{BearerPrefix} {tokenStringValues[0]}" });
                    }

                if (context.Request.Cookies.TryGetValue(CookieName, out var token))
                    if (!string.IsNullOrWhiteSpace(token))
                        context.Request.Headers.Add(AuthorizationHeaderName, new[] { $"{BearerPrefix} {token}" });
            }
            catch
            {
                // if multiple headers it may throw an error.  Ignore both.
            }

        await _next(context);
    }
}