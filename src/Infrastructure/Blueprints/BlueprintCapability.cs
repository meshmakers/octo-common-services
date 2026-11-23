namespace Meshmakers.Octo.Services.Infrastructure.Blueprints;

/// <summary>
///     A tenant capability that the platform switches on when a blueprint needs it.
/// </summary>
public enum BlueprintCapability
{
    /// <summary>The Communication Controller (<c>EnableCommunication</c>).</summary>
    Communication,

    /// <summary>The AI service (<c>EnableAi</c>); needs Communication first.</summary>
    Ai
}

/// <summary>
///     Which CK model dependency of a blueprint means which capability.
/// </summary>
/// <remarks>
///     Names match exactly and case-sensitively, like CK model names in the catalogs: a model named
///     <c>System.Communication.Extra</c> or <c>MySystem.Ai</c> needs nothing. <c>System.Ai</c> depends on
///     <c>System.Communication</c>, so a blueprint that needs AI needs Communication too.
/// </remarks>
public static class BlueprintCapabilityRequirements
{
    /// <summary>CK model whose presence in the dependencies of a blueprint needs Communication.</summary>
    public const string CommunicationCkModel = "System.Communication";

    /// <summary>CK model whose presence in the dependencies of a blueprint needs AI (and Communication).</summary>
    public const string AiCkModel = "System.Ai";

    /// <summary>
    ///     Whether a blueprint with the given CK model dependencies needs <paramref name="capability" />.
    ///     <c>null</c> (the publisher did not report them) never needs anything.
    /// </summary>
    public static bool IsRequired(BlueprintCapability capability, IReadOnlyCollection<string>? ckModelDependencies)
    {
        if (ckModelDependencies == null)
        {
            return false;
        }

        var needsAi = ckModelDependencies.Contains(AiCkModel, StringComparer.Ordinal);
        return capability switch
        {
            BlueprintCapability.Ai => needsAi,
            BlueprintCapability.Communication => needsAi ||
                                                 ckModelDependencies.Contains(CommunicationCkModel,
                                                     StringComparer.Ordinal),
            _ => false
        };
    }
}
