using System.Security.Claims;
using Meshmakers.Octo.Sdk.ServiceClient.Authorization;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Common.Authorization;

/// <summary>
///     Middleware to add information of the user info endpoint to HttpContext.User to add
///     username and roles
/// </summary>
public class UserInfoMiddleware
{
    private readonly IUserInfoCache _userInfoCache;
    private readonly RequestDelegate _next;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="next">Next delegate</param>
    /// <param name="userInfoCache"></param>
    public UserInfoMiddleware(RequestDelegate next, IUserInfoCache userInfoCache)
    {
        _next = next;
        _userInfoCache = userInfoCache;
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
            var userInfoData = await _userInfoCache.GetUserInfoAsync(bearerToken);
            if (userInfoData is { IsAuthenticated: true, Claims: not null })
            {
                var claimsIdentity = (ClaimsIdentity)httpContext.User.Identity!;
                claimsIdentity.AddClaims(userInfoData.Claims);
            }
        }

        await _next(httpContext);
    }
}