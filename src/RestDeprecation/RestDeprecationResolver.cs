using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     The header values and dates of one <see cref="OctoRestDeprecatedAttribute" />, after the wave configuration has
///     been applied. A <c>null</c> header is not written.
/// </summary>
internal sealed record RestDeprecationValues(
    string? SinceDay,
    string? SunsetDay,
    string? DeprecationHeader,
    string? SunsetHeader,
    string LinkHeader,
    string? SuccessorLinkHeader);

/// <summary>
///     Resolves an <see cref="OctoRestDeprecatedAttribute" /> to its header values: the attribute's own dates, or — with
///     <see cref="OctoRestDeprecatedAttribute.Wave" /> — the dates of <see cref="RestDeprecationOptions" /> (AB#8415).
/// </summary>
internal sealed class RestDeprecationResolver
{
    // Endpoint metadata instances live as long as the endpoint, so the values are computed once per attribute instance.
    // Reference equality: Attribute.Equals compares the field values reflectively.
    private readonly ConcurrentDictionary<OctoRestDeprecatedAttribute, RestDeprecationValues> _values =
        new(ReferenceEqualityComparer.Instance);

    private readonly RestDeprecationOptions _options;

    public RestDeprecationResolver(IOptions<RestDeprecationOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>A resolver without wave configuration (hosts that never called <c>AddOctoRestDeprecation()</c>).</summary>
    internal static RestDeprecationResolver Unconfigured { get; } =
        new(Options.Create(new RestDeprecationOptions()));

    /// <summary>
    ///     Checks the attribute and, for a wave, its configuration; returns <c>null</c> when both are valid, otherwise the
    ///     reason.
    /// </summary>
    public string? Validate(OctoRestDeprecatedAttribute attribute)
    {
        var error = attribute.Validate();
        if (error != null || !attribute.HasWave)
        {
            return error;
        }

        var wave = _options.GetWave(attribute.Wave!);
        var prefix = $"{RestDeprecationOptions.SectionName}:Waves:{attribute.Wave}";
        return wave == null
            ? null
            : OctoRestDeprecatedAttribute.ValidateDates(prefix + ":Since", wave.Since, prefix + ":Sunset", wave.Sunset,
                required: false);
    }

    /// <summary>The header values of <paramref name="attribute" />; throws when it or its wave configuration is invalid.</summary>
    public RestDeprecationValues Resolve(OctoRestDeprecatedAttribute attribute)
    {
        return _values.GetOrAdd(attribute, Create);
    }

    private RestDeprecationValues Create(OctoRestDeprecatedAttribute attribute)
    {
        var error = Validate(attribute);
        if (error != null)
        {
            throw new InvalidOperationException($"[OctoRestDeprecated] is invalid: {error}");
        }

        string? since, sunset;
        if (attribute.HasWave)
        {
            var wave = _options.GetWave(attribute.Wave!);
            since = NullIfEmpty(wave?.Since);
            sunset = NullIfEmpty(wave?.Sunset);
        }
        else
        {
            since = attribute.Since;
            sunset = attribute.Sunset;
        }

        return new RestDeprecationValues(
            since,
            sunset,
            since == null ? null : "@" + ParseDate(since).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            sunset == null ? null : ParseDate(sunset).ToString("r", CultureInfo.InvariantCulture),
            $"<{attribute.Link}>; rel=\"deprecation\"; type=\"text/html\"",
            string.IsNullOrEmpty(attribute.Successor) ? null : $"<{attribute.Successor}>; rel=\"successor-version\"");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset ParseDate(string value)
    {
        OctoRestDeprecatedAttribute.TryParseDate(value, out var date);
        return date;
    }
}
