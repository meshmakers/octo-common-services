using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NLog;

namespace Meshmakers.Octo.Services.Common.Authorization;

/// <summary>
///     Middleware for authorization in case not MVC is used (for hangfire and GraphQL web tools)
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class AuthorizationMiddleware
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly RequestDelegate _next;
    private readonly string _policyName;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next">Next delegate</param>
    /// <param name="policyName">Policy name that is applied</param>
    public AuthorizationMiddleware(RequestDelegate next, string policyName)
    {
        _next = next;
        _policyName = policyName;
    }

    /// <summary>
    ///     Invokes the middleware
    /// </summary>
    /// <param name="httpContext"></param>
    /// <param name="authorizationService"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext httpContext, IAuthorizationService authorizationService)
    {
        var authorizationResult =
            await authorizationService.AuthorizeAsync(httpContext.User, null, _policyName);

        if (!authorizationResult.Succeeded)
        {
            Logger.Warn("User autorization failed.");
            foreach (var userClaim in httpContext.User.Claims) Logger.Warn($"{userClaim.Type}={userClaim.Value}");

            if (httpContext.User.Identity != null && httpContext.User.Identity.IsAuthenticated)
            {
                Logger.Warn("Authenticated, forbid access...");
                await httpContext.ForbidAsync();
            }
            else
            {
                Logger.Warn("Not authenticated, challenging...");
                await httpContext.ChallengeAsync();
            }

            return;
        }

        await _next(httpContext);
    }
}