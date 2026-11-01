using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Infrastructure.Middleware;

namespace Meshmakers.Octo.Services.Infrastructure.Configuration;

/// <summary>
/// Extensions for the application builder
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds authorization for the current application builder
    /// </summary>
    /// <param name="app"></param>
    /// <param name="policyName"></param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseAuthorization(this IApplicationBuilder app, string policyName)
    {
        // Null checks removed for brevity
        ArgumentValidation.ValidateString(nameof(policyName), policyName);

        return app.UseMiddleware<AuthorizationMiddleware>(policyName);
    }

    /// <summary>
    ///     Adds authorization by calling the user info endpoint and add claims to HttpContext.User
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseOctoAuthorizationUserInfo(this IApplicationBuilder app)
    {
        return app.UseMiddleware<UserInfoMiddleware>();
    }

    /// <summary>
    ///     Default path of <see cref="UseOctoCookieBasedAuthentication(IApplicationBuilder)" />: the branch the
    ///     Hangfire dashboard of bot-services is mapped to.
    /// </summary>
    public const string DefaultCookieBasedAuthenticationPath = "/ui";

    /// <summary>
    ///     Adds authentication using jwt tokens from the request (<c>?jwt_token=</c> or the
    ///     <c>OctoIdentityAccessToken</c> cookie), limited to <see cref="DefaultCookieBasedAuthenticationPath" />.
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseOctoCookieBasedAuthentication(this IApplicationBuilder app)
    {
        return app.UseOctoCookieBasedAuthentication(DefaultCookieBasedAuthenticationPath);
    }

    /// <summary>
    ///     Adds authentication using jwt tokens from the request (<c>?jwt_token=</c> or the
    ///     <c>OctoIdentityAccessToken</c> cookie), limited to requests below <paramref name="path" />.
    /// </summary>
    /// <remarks>
    ///     AB#7270: the cookie is set <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c> with
    ///     <c>Path</c> = <paramref name="path" />; outside that path the middleware does nothing.
    /// </remarks>
    /// <param name="app"></param>
    /// <param name="path">Path of the browser surface that needs the token cookie, e.g. <c>/ui</c>; not the root.</param>
    /// <returns></returns>
    public static IApplicationBuilder UseOctoCookieBasedAuthentication(this IApplicationBuilder app, PathString path)
    {
        return app.UseMiddleware<CookieBasedAuthenticationMiddleware>(path);
    }

    /// <summary>
    ///     Adds authorization for the current application builder
    /// </summary>
    /// <param name="app"></param>
    /// <param name="policyName"></param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOctoTenants(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantMiddleware>();
    }

    /// <summary>
    ///     Adds tenant authorization middleware that validates the route tenant
    ///     against the user's tenant_id claim from the access token.
    ///     Must be placed after UseAuthentication() and UseAuthorization().
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseOctoTenantAuthorization(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantAuthorizationMiddleware>();
    }
}
