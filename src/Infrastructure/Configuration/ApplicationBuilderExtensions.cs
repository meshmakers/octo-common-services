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
    public static IApplicationBuilder UseAuthorizationUserInfo(this IApplicationBuilder app)
    {
        return app.UseMiddleware<UserInfoMiddleware>();
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
}
