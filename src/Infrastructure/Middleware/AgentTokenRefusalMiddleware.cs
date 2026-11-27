using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;

namespace Meshmakers.Octo.Services.Infrastructure.Middleware;

/// <summary>
///     Refuses agent tokens (scope <c>octo_agent</c>, AB#6054) on every endpoint that is not marked
///     <see cref="IAllowAgentTokens" />: <c>403</c> with the error code <see cref="AgentTokens.NotAcceptedErrorCode" />.
/// </summary>
/// <remarks>
///     <para>
///         It runs inside <c>UseOctoTenantAuthorization()</c>, before the tenant gate and before every early exit of it
///         (anonymous endpoints, cookie principals, routes without a tenant): an agent token reaches no endpoint but the
///         MCP agent endpoint, whatever the route looks like.
///     </para>
///     <para>
///         The principal on <see cref="HttpContext.User" /> is not always the bearer principal: hosts whose default
///         scheme is the cookie (identity, bot) authenticate the bearer token only for endpoints whose policy names the
///         scheme, and SignalR clients send the token as <c>?access_token=</c>. So when bearer credentials are present
///         and the host has the <see cref="BearerScheme" /> scheme, the scheme is authenticated here as well (the handler
///         caches its result per request, so a request it already authenticated costs nothing).
///     </para>
/// </remarks>
internal sealed class AgentTokenRefusalMiddleware(RequestDelegate next, ILogger<AgentTokenRefusalMiddleware> logger)
{
    /// <summary>Name of the JWT bearer scheme the OctoMesh services register (<c>JwtBearerDefaults.AuthenticationScheme</c>).</summary>
    internal const string BearerScheme = "Bearer";

    private const string AccessTokenQueryParameter = "access_token";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAgentTokens>() == null &&
            await PresentsAgentTokenAsync(context))
        {
            logger.LogWarning("Agent token refused on {Method} {Path}: {ErrorCode} (client {ClientId})",
                context.Request.Method, context.Request.Path, AgentTokens.NotAcceptedErrorCode,
                context.User.FindFirst("client_id")?.Value);
            await AgentTokens.WriteRefusalAsync(context);
            return;
        }

        await next(context);
    }

    private static async Task<bool> PresentsAgentTokenAsync(HttpContext context)
    {
        if (AgentTokens.IsAgentToken(context.User))
        {
            return true;
        }

        if (!HasBearerCredentials(context.Request))
        {
            return false;
        }

        var schemes = context.RequestServices?.GetService<IAuthenticationSchemeProvider>();
        if (schemes == null || await schemes.GetSchemeAsync(BearerScheme) == null)
        {
            return false;
        }

        var result = await context.AuthenticateAsync(BearerScheme);
        return result.Succeeded && AgentTokens.IsAgentToken(result.Principal);
    }

    private static bool HasBearerCredentials(HttpRequest request)
    {
        return request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrEmpty(request.Query[AccessTokenQueryParameter].ToString());
    }
}
