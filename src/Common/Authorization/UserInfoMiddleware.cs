using System.Security.Claims;
using Meshmakers.Octo.Sdk.ServiceClient.Authorization;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Common.Authorization;

/// <summary>
///     Middleware to add information of the user info endpoint to HttpContext.User to add
///     user name and roles
/// </summary>
public class UserInfoMiddleware
{
    private readonly IAuthorizationClient _authorizationClient;
    private readonly RequestDelegate _next;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next">Next delegate</param>
    /// <param name="authorizationClient"></param>
    public UserInfoMiddleware(RequestDelegate next, IAuthorizationClient authorizationClient)
    {
        _next = next;
        _authorizationClient = authorizationClient;
    }

    /// <summary>
    ///     Invokes the middleware
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext httpContext)
    {
        if (httpContext.TryGetBearerAccessToken(out var bearerToken) && !string.IsNullOrWhiteSpace(bearerToken))
        {
            var userInfoData = await _authorizationClient.GetUserInfoAsync(bearerToken);
            if (userInfoData.IsAuthenticated && userInfoData.Claims != null)
            {
                var claimsIdentity = (ClaimsIdentity)httpContext.User.Identity!;
                claimsIdentity.AddClaims(userInfoData.Claims);
            }
        }

        await _next(httpContext);
    }
}