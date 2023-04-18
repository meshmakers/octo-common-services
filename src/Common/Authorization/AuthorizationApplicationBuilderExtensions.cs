using Meshmakers.Common.Shared;
using Microsoft.AspNetCore.Builder;

namespace Meshmakers.Octo.Backend.Common.Authorization;

/// <summary>
///     Extensions of Application Builder for non-mvc based authorization
/// </summary>
public static class AuthorizationApplicationBuilderExtensions
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
    /// Adds authorization by calling the user info endpoint and add claims to HttpContext.User
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static IApplicationBuilder UseAuthorizationUserInfo(this IApplicationBuilder app)
    {
        return app.UseMiddleware<UserInfoMiddleware>();
    }
}
