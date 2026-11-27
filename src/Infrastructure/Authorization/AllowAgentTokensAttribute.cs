using Microsoft.AspNetCore.Builder;

namespace Meshmakers.Octo.Services.Infrastructure.Authorization;

/// <summary>
///     Endpoint metadata: this endpoint accepts agent tokens (scope <see cref="AgentTokens.AgentScope" />, AB#6054).
///     Every endpoint without it refuses them with <c>403</c> <see cref="AgentTokens.NotAcceptedErrorCode" />.
/// </summary>
/// <remarks>
///     🔴 Belongs on the MCP agent endpoint only (both <c>MapMcp</c> routes of the MCP service), where the mandate guard
///     checks every call of the agent and every downstream call uses an exchanged token. Any other endpoint that carries
///     it lets an agent act beside its mandate.
/// </remarks>
public interface IAllowAgentTokens;

/// <summary>
///     Marks a controller or action as accepting agent tokens. See <see cref="IAllowAgentTokens" /> for the rule.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class AllowAgentTokensAttribute : Attribute, IAllowAgentTokens;

/// <summary>
///     Opt-in of a minimal-API or mapped endpoint (e.g. <c>MapMcp</c>) into accepting agent tokens.
/// </summary>
public static class AllowAgentTokensEndpointConventionBuilderExtensions
{
    /// <summary>
    ///     Adds <see cref="IAllowAgentTokens" /> to the endpoints of <paramref name="builder" />. See
    ///     <see cref="IAllowAgentTokens" /> before you use it.
    /// </summary>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <typeparam name="TBuilder">Type of the builder.</typeparam>
    /// <returns>The builder.</returns>
    public static TBuilder AllowAgentTokens<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(b => b.Metadata.Add(new AllowAgentTokensAttribute()));
        return builder;
    }
}
