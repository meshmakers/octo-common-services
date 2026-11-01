using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Infrastructure.Tests.Middleware;

/// <summary>
///     AB#7270: <see cref="CookieBasedAuthenticationMiddleware" /> stores a raw bearer token in the
///     <c>OctoIdentityAccessToken</c> cookie for the Hangfire dashboard. The cookie must be unreadable for scripts,
///     https-only, never sent cross-site and confined to the dashboard path; outside that path neither the query
///     token nor the cookie may become an Authorization header.
/// </summary>
public class CookieBasedAuthenticationMiddlewareTests
{
    private const string DashboardPath = "/ui";
    private const string Token = "header.payload.signature";

    private static async Task<(HttpContext Context, bool NextCalled)> InvokeAsync(string path, string? query = null,
        string? cookie = null, string? authorization = null, string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        if (query != null)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (cookie != null)
        {
            context.Request.Headers.Cookie = cookie;
        }

        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        var nextCalled = false;
        var middleware = new CookieBasedAuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, DashboardPath);

        await middleware.Invoke(context);
        return (context, nextCalled);
    }

    private static SetCookieHeaderValue SingleSetCookie(HttpContext context)
    {
        var headers = context.Response.Headers.SetCookie;
        Assert.Single(headers);
        return SetCookieHeaderValue.Parse(headers[0]);
    }

    [Fact]
    public async Task QueryToken_OnDashboardPath_SetsHardenedCookie()
    {
        var (context, nextCalled) = await InvokeAsync("/ui/jobs", $"?jwt_token={Token}");

        Assert.True(nextCalled);
        var cookie = SingleSetCookie(context);
        Assert.Equal(CookieBasedAuthenticationMiddleware.CookieName, cookie.Name.Value);
        Assert.Equal(Token, cookie.Value.Value);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal(DashboardPath, cookie.Path.Value);
        Assert.Equal($"Bearer {Token}", context.Request.Headers.Authorization.ToString());
    }

    [Fact]
    public async Task QueryToken_BelowPathBase_CookiePathIncludesPathBase()
    {
        var (context, _) = await InvokeAsync("/ui/jobs", $"?jwt_token={Token}", pathBase: "/bots");

        Assert.Equal("/bots/ui", SingleSetCookie(context).Path.Value);
    }

    [Fact]
    public async Task Cookie_OnDashboardPath_BecomesAuthorizationHeader()
    {
        var (context, _) = await InvokeAsync("/ui/jobs/stats", cookie: $"{CookieBasedAuthenticationMiddleware.CookieName}={Token}");

        Assert.Equal($"Bearer {Token}", context.Request.Headers.Authorization.ToString());
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData("/system/v1/jobs")]
    [InlineData("/meshtest/v1/jobs/dump-repository")]
    [InlineData("/uiother")]
    [InlineData("/")]
    public async Task OutsideDashboardPath_NeitherQueryTokenNorCookieIsUsed(string path)
    {
        var (queryContext, queryNext) = await InvokeAsync(path, $"?jwt_token={Token}");
        var (cookieContext, cookieNext) = await InvokeAsync(path, cookie: $"{CookieBasedAuthenticationMiddleware.CookieName}={Token}");

        Assert.True(queryNext);
        Assert.True(cookieNext);
        Assert.Equal(0, queryContext.Response.Headers.SetCookie.Count);
        Assert.True(string.IsNullOrEmpty(queryContext.Request.Headers.Authorization));
        Assert.True(string.IsNullOrEmpty(cookieContext.Request.Headers.Authorization));
    }

    [Fact]
    public async Task ExistingAuthorizationHeader_IsLeftAlone()
    {
        var (context, _) = await InvokeAsync("/ui/jobs", $"?jwt_token={Token}", authorization: "Bearer other");

        Assert.Equal("Bearer other", context.Request.Headers.Authorization.ToString());
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    public void RootOrEmptyPath_IsRefused(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            new CookieBasedAuthenticationMiddleware(_ => Task.CompletedTask, new PathString(path)));
    }
}
