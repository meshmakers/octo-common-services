using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Writes the reason of a refused <see cref="TenantRoleRequirement" /> into the <c>403</c> response (AB#7319), in the
///     shape of the tenant gate's denials (AB#5227): a JSON body <c>{"message": "..."}</c>, which the frontends' HTTP error
///     interceptor renders. Every other outcome is left to the framework's default handler.
/// </summary>
public sealed class ForbiddenReasonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && !context.Response.HasStarted)
        {
            var reason = authorizeResult.AuthorizationFailure?.FailureReasons
                .FirstOrDefault(r => r.Handler is TenantRoleAuthorizationHandler);
            if (reason != null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { message = reason.Message });
                return;
            }
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
