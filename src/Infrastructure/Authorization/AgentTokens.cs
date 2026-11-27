using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Agent tokens (AB#6054): an access token whose <c>scope</c> contains <see cref="AgentScope" /> belongs to an
///     agent. An agent acts only through its MCP mandate, so such a token is accepted by exactly one kind of endpoint —
///     the MCP agent endpoint, which opts in with <see cref="IAllowAgentTokens" /> — and refused by every other
///     OctoMesh API with <c>403</c> and the error code <see cref="NotAcceptedErrorCode" />.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Deny by default.</b> The rule refuses <c>octo_agent</c>; it does not demand <c>octo_api</c>. Several
///         read paths (GraphQL queries of the asset repository, for example) require no scope at all, so an agent token
///         used to read tenant data there directly, beside its mandate. Making <c>octo_api</c> mandatory everywhere would
///         break existing clients; refusing the agent scope does not.
///     </para>
///     <para>
///         The rule holds for every kind of token that carries the scope (client and user tokens alike). Both wire
///         encodings of <c>scope</c> are read: one claim per scope and one space-delimited claim.
///     </para>
/// </remarks>
public static class AgentTokens
{
    /// <summary>The scope that marks an agent token.</summary>
    public const string AgentScope = "octo_agent";

    /// <summary>The error code of the <c>403</c> answer to an agent token outside the MCP agent endpoint.</summary>
    public const string NotAcceptedErrorCode = "AgentTokenNotAccepted";

    /// <summary>The message of the <c>403</c> answer; it names no claim value of the token.</summary>
    public const string NotAcceptedMessage =
        "Agent tokens (scope octo_agent) are accepted only by the MCP agent endpoint. An agent acts only through its MCP mandate.";

    /// <summary>
    ///     <c>true</c> when <paramref name="principal" /> carries the scope <see cref="AgentScope" />, in either
    ///     encoding of the <c>scope</c> claim.
    /// </summary>
    /// <param name="principal">A validated principal, or <c>null</c>.</param>
    public static bool IsAgentToken(ClaimsPrincipal? principal)
    {
        if (principal == null)
        {
            return false;
        }

        foreach (var claim in principal.FindAll(InfrastructureCommon.ClaimScope))
        {
            foreach (var value in claim.Value.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(value, AgentScope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Writes the refusal: <c>403</c> with the JSON body
    ///     <c>{"statusCode":403,"statusDescription":"Forbidden","code":"AgentTokenNotAccepted","message":"…"}</c> (the
    ///     fields of the API error body plus <c>code</c>; <c>message</c> is what the tenant gate's answers carry too).
    /// </summary>
    /// <param name="context">The request to refuse.</param>
    public static async Task WriteRefusalAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new AgentTokenRefusalBody(StatusCodes.Status403Forbidden, "Forbidden",
            NotAcceptedErrorCode, NotAcceptedMessage));
    }

    private sealed record AgentTokenRefusalBody(int StatusCode, string StatusDescription, string Code, string Message);
}
