namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     Configuration of the REST deprecation mechanism, section <c>RestDeprecation</c> (AB#8415).
/// </summary>
/// <remarks>
///     <para>
///         Holds the dates of the deprecation waves that <see cref="OctoRestDeprecatedAttribute.Wave" /> refers to. The
///         dates of a wave are only known at rollout (Deprecation: the release containing the wave is delivered; Sunset:
///         decided after the rollout), so they live in the service configuration — in the cluster as environment
///         variables, e.g. <c>OCTO_RestDeprecation__Waves__IdentityUsers1__Since=2026-12-01</c>, set from Helm values.
///     </para>
///     <para>The values are read once at startup; a change needs a restart (a Helm value change rolls the pods).</para>
/// </remarks>
public sealed class RestDeprecationOptions
{
    /// <summary>Name of the configuration section.</summary>
    public const string SectionName = "RestDeprecation";

    /// <summary>Dates per wave name (case-insensitive).</summary>
    public Dictionary<string, RestDeprecationWaveOptions> Waves { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The configuration of <paramref name="wave" />, or <c>null</c> when none is configured.</summary>
    internal RestDeprecationWaveOptions? GetWave(string wave)
    {
        if (Waves.TryGetValue(wave, out var options))
        {
            return options;
        }

        // The binder may have replaced the dictionary with one without the case-insensitive comparer.
        return Waves.FirstOrDefault(w => string.Equals(w.Key, wave, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

/// <summary>The dates of one deprecation wave; each is optional.</summary>
public sealed class RestDeprecationWaveOptions
{
    /// <summary>
    ///     Day (UTC, <c>yyyy-MM-dd</c>) the operations of the wave are deprecated; drives the <c>Deprecation</c> header. Not
    ///     set: no <c>Deprecation</c> header.
    /// </summary>
    public string? Since { get; set; }

    /// <summary>
    ///     Day (UTC, <c>yyyy-MM-dd</c>) after which the operations may be removed; drives the <c>Sunset</c> header. Not set:
    ///     no <c>Sunset</c> header. Must be after <see cref="Since" /> when both are set.
    /// </summary>
    public string? Sunset { get; set; }
}
