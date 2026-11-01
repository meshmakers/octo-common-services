namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Allows to transform a jwt token to a cookie based auth
/// </summary>
/// <remarks>
///     <para>
///         The Hangfire dashboard is opened as a browser navigation (<c>…/ui/jobs?jwt_token=…</c>), so its
///         follow-up requests (stats polling, commands, assets) carry no Authorization header. This middleware
///         turns the query token into the Authorization header of the first request and stores it in the
///         <c>OctoIdentityAccessToken</c> cookie for the follow-up requests.
///     </para>
///     <para>
///         AB#7270: the cookie holds a raw bearer token, so it is confined as far as the flow allows —
///         <c>HttpOnly</c> (no script can read it), <c>Secure</c>, <c>SameSite=Strict</c> (never sent on
///         cross-site requests; the dashboard's follow-ups are same-origin) and <c>Path</c> = the dashboard
///         path. The middleware only acts on requests below that path: elsewhere neither <c>?jwt_token=</c>
///         nor the cookie becomes an Authorization header, so API routes stay bearer-header only.
///     </para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
internal class CookieBasedAuthenticationMiddleware
{
    /// <summary>Name of the cookie that carries the access token.</summary>
    internal const string CookieName = "OctoIdentityAccessToken";

    /// <summary>Query parameter that carries the access token on the entry request.</summary>
    internal const string TokenQueryParameterName = "jwt_token";

    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerPrefix = "Bearer";
    private readonly RequestDelegate _next;
    private readonly PathString _scopePath;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next">Next middleware</param>
    /// <param name="scopePath">
    ///     Path the token conversion is limited to (e.g. <c>/ui</c> for the Hangfire dashboard); also the
    ///     <c>Path</c> of the cookie. The root path is refused: it would send the token with every request.
    /// </param>
    public CookieBasedAuthenticationMiddleware(RequestDelegate next, PathString scopePath)
    {
        if (!scopePath.HasValue || scopePath == "/")
        {
            throw new ArgumentException("The cookie path must be a sub path such as '/ui', not the root.", nameof(scopePath));
        }

        _next = next;
        _scopePath = scopePath;
    }

    /// <summary>
    ///     Invokes the middleware
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(_scopePath, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(context.Request.Headers[AuthorizationHeaderName]))
        {
            try
            {
                if (context.Request.Query.TryGetValue(TokenQueryParameterName, out var tokenStringValues))
                {
                    if (tokenStringValues.Count > 0)
                    {
                        context.Response.Cookies.Append(CookieName, tokenStringValues[0] ?? string.Empty,
                            CreateCookieOptions(context));
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

    private CookieOptions CreateCookieOptions(HttpContext context)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            // TLS ends at the ingress; the browser always talks https to the service.
            Secure = true,
            SameSite = SameSiteMode.Strict,
            // PathBase keeps the cookie on the dashboard when the service is hosted below a base path.
            Path = (context.Request.PathBase + _scopePath).Value,
            IsEssential = true
        };
    }
}
