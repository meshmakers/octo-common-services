namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Allows to transform a jwt token to a cookie based auth
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class CookieBasedAuthenticationMiddleware
{
    private const string CookieName = "OctoIdentityAccessToken";
    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerPrefix = "Bearer";
    private readonly RequestDelegate _next;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next"></param>
    public CookieBasedAuthenticationMiddleware(RequestDelegate next)
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
        {
            try
            {
                if (context.Request.Query.TryGetValue("jwt_token", out var tokenStringValues))
                {
                    if (tokenStringValues.Count > 0)
                    {
                        context.Response.Cookies.Append(CookieName, tokenStringValues[0] ?? string.Empty);
                        context.Request.Headers.Append(AuthorizationHeaderName, new[] { $"{BearerPrefix} {tokenStringValues[0]}" });
                    }
                }
                else if (context.Request.Cookies.TryGetValue(CookieName, out var token))
                {
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        context.Request.Headers.Append(AuthorizationHeaderName, new[] { $"{BearerPrefix} {token}" });
                    }
                }
            }
            catch
            {
                // if multiple headers it may throw an error.  Ignore both.
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}