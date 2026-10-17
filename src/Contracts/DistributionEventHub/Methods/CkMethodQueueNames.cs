using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Queue (command) names of CK methods. The event hub prepends the instance prefix.
/// </summary>
public static partial class CkMethodQueueNames
{
    /// <summary>
    ///     Common prefix of all CK method queues.
    /// </summary>
    public const string Prefix = "octo::method::";

    /// <summary>
    ///     Returns the queue name of a qualified method id: <c>octo::method::{model}::{type}::{method}-{v}</c>,
    ///     lower-case and model-version-less. The queue is keyed by the DECLARING type of the method.
    /// </summary>
    /// <example>
    ///     <c>System.Identity/User.ChangePassword-1</c> → <c>octo::method::system.identity::user::changepassword-1</c>
    /// </example>
    /// <param name="qualifiedMethodId">
    ///     <c>{model}/{type}.{method}-{version}</c>; a model version (<c>System.Identity-2.90.0</c>,
    ///     <c>System.Identity@2</c>) is ignored.
    /// </param>
    /// <returns>The queue name</returns>
    /// <exception cref="ArgumentException">The id is not a qualified method id.</exception>
    public static string For(string qualifiedMethodId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedMethodId);

        var slash = qualifiedMethodId.IndexOf('/');
        var dot = qualifiedMethodId.LastIndexOf('.');
        if (slash <= 0 || dot <= slash + 1 || dot == qualifiedMethodId.Length - 1)
        {
            throw new ArgumentException(
                $"'{qualifiedMethodId}' is not a qualified CK method id ('{{model}}/{{type}}.{{method}}-{{version}}').",
                nameof(qualifiedMethodId));
        }

        var model = StripModelVersion(qualifiedMethodId[..slash]);
        var type = qualifiedMethodId[(slash + 1)..dot];
        var method = qualifiedMethodId[(dot + 1)..];
        if (!MethodWithVersionRegex().IsMatch(method))
        {
            throw new ArgumentException(
                $"'{qualifiedMethodId}' has no method version ('{{method}}-{{version}}', e.g. 'ChangePassword-1').",
                nameof(qualifiedMethodId));
        }

        return $"{Prefix}{model}::{type}::{method}".ToLowerInvariant();
    }

    /// <summary>
    ///     Returns the model name of a qualified method id without a model version,
    ///     e.g. <c>System.Identity-2.90.0/User.ChangePassword-1</c> → <c>System.Identity</c>.
    /// </summary>
    /// <param name="qualifiedMethodId">Qualified method id</param>
    /// <returns>The model name</returns>
    public static string GetModelName(string qualifiedMethodId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedMethodId);
        var slash = qualifiedMethodId.IndexOf('/');
        return StripModelVersion(slash > 0 ? qualifiedMethodId[..slash] : qualifiedMethodId);
    }

    private static string StripModelVersion(string model)
    {
        var at = model.IndexOf('@');
        if (at > 0)
        {
            return model[..at];
        }

        var match = ModelSemVerSuffixRegex().Match(model);
        return match.Success ? model[..match.Index] : model;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*-\d+$")]
    private static partial Regex MethodWithVersionRegex();

    [GeneratedRegex(@"-\d+\.\d+\.\d+.*$")]
    private static partial Regex ModelSemVerSuffixRegex();
}
