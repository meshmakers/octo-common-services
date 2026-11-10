using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     Marks a REST action (or every action of a controller) as deprecated (AB#8421, F5.7 of CK v2 Phase 5).
/// </summary>
/// <remarks>
///     <para>
///         With the attribute in place, and no further code in the service, every response of the action carries the
///         headers <c>Deprecation: @&lt;unix seconds of Since&gt;</c> (RFC 9745), <c>Sunset: &lt;IMF-fixdate&gt;</c>
///         (RFC 8594) and <c>Link: &lt;Link&gt;; rel="deprecation"; type="text/html"</c> (RFC 9745) — also on 4xx and
///         5xx answers —, the operation is <c>deprecated: true</c> in the OpenAPI document and each call increments
///         <c>octo.rest.deprecated.calls</c> (meter <see cref="RestDeprecationMetrics.MeterName" />).
///     </para>
///     <para>
///         <b>Dates in code.</b> Without <see cref="Wave" />, <see cref="Since" />, <see cref="Sunset" /> and
///         <see cref="Link" /> are required: <see cref="Since" /> and <see cref="Sunset" /> as <c>yyyy-MM-dd</c> (UTC
///         midnight), <see cref="Sunset" /> after <see cref="Since" />, and <see cref="Link" /> an absolute http(s) URL.
///     </para>
///     <para>
///         <b>Dates in configuration (AB#8415).</b> With <see cref="Wave" />, the dates come from the configuration
///         section <c>RestDeprecation:Waves:&lt;Wave&gt;</c> (<see cref="RestDeprecationOptions" />), because they are only
///         known at rollout: <c>Since</c> drives the <c>Deprecation</c> header, <c>Sunset</c> the <c>Sunset</c> header, and
///         each header is left out while its date is not configured. <see cref="Since" /> and <see cref="Sunset" /> must
///         then stay empty; <see cref="Link" /> is required. The <c>Link</c> header, the OpenAPI flag and the counter work
///         from the first deployment on, with or without dates.
///     </para>
///     <para>
///         An invalid attribute or wave configuration fails the service at startup with a message naming the action.
///     </para>
///     <para>
///         Optional: <see cref="Successor" />, an absolute http(s) URL of the successor operation. When set, the responses
///         carry the further Link value <c>&lt;Successor&gt;; rel="successor-version"</c> (RFC 5829); it is validated at
///         startup like <see cref="Link" />.
///     </para>
///     <para>See <c>docs/rest-deprecation.md</c> in octo-common-services.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed partial class OctoRestDeprecatedAttribute : Attribute
{
    internal const string DateFormat = "yyyy-MM-dd";

    /// <summary>Day (UTC, <c>yyyy-MM-dd</c>) the operation became deprecated, normally GA of the replacing method.</summary>
    /// <remarks>Must stay empty when <see cref="Wave" /> is set; the wave's configuration carries the date then.</remarks>
    public string Since { get; set; } = string.Empty;

    /// <summary>Day (UTC, <c>yyyy-MM-dd</c>) after which the operation may be removed; must be after <see cref="Since" />.</summary>
    /// <remarks>Must stay empty when <see cref="Wave" /> is set; the wave's configuration carries the date then.</remarks>
    public string Sunset { get; set; } = string.Empty;

    /// <summary>Absolute http(s) URL of the documentation of the replacing operation (for example the CK method).</summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>
    ///     Optional absolute http(s) URL of the successor operation; adds <c>Link: &lt;url&gt;; rel="successor-version"</c>
    ///     (RFC 5829) to every response.
    /// </summary>
    public string? Successor { get; set; }

    /// <summary>
    ///     Optional name of the deprecation wave (letters and digits, starting with a letter, e.g. <c>IdentityUsers1</c>).
    ///     When set, <see cref="Since" /> and <see cref="Sunset" /> are read from the configuration section
    ///     <c>RestDeprecation:Waves:&lt;Wave&gt;</c> instead of the attribute (AB#8415); a date that is not configured
    ///     leaves its header out.
    /// </summary>
    public string? Wave { get; set; }

    /// <summary>Whether the dates come from the configuration of <see cref="Wave" />.</summary>
    internal bool HasWave => !string.IsNullOrEmpty(Wave);

    /// <summary>
    ///     Checks the values of the attribute itself (not the wave configuration); returns <c>null</c> when they are valid,
    ///     otherwise the reason.
    /// </summary>
    internal string? Validate()
    {
        if (HasWave)
        {
            if (!WaveNamePattern().IsMatch(Wave!))
            {
                return $"Wave '{Wave}' must consist of letters and digits and start with a letter";
            }

            if (!string.IsNullOrEmpty(Since) || !string.IsNullOrEmpty(Sunset))
            {
                return $"Since and Sunset must not be set together with Wave '{Wave}'; configure them in " +
                       $"{RestDeprecationOptions.SectionName}:Waves:{Wave}";
            }
        }
        else
        {
            var dateError = ValidateDates(nameof(Since), Since, nameof(Sunset), Sunset, required: true);
            if (dateError != null)
            {
                return dateError;
            }
        }

        return ValidateUrl(nameof(Link), Link) ??
               (string.IsNullOrEmpty(Successor) ? null : ValidateUrl(nameof(Successor), Successor));
    }

    /// <summary>
    ///     Checks a pair of dates: format <c>yyyy-MM-dd</c> and sunset after since. With <paramref name="required" /> both
    ///     must be present; otherwise each may be empty and the order is checked only when both are set.
    /// </summary>
    internal static string? ValidateDates(string sinceName, string? since, string sunsetName, string? sunset,
        bool required)
    {
        DateTimeOffset sinceDate = default, sunsetDate = default;
        if ((required || !string.IsNullOrEmpty(since)) && !TryParseDate(since, out sinceDate))
        {
            return $"{sinceName} '{since}' is not a date in the format {DateFormat}";
        }

        if ((required || !string.IsNullOrEmpty(sunset)) && !TryParseDate(sunset, out sunsetDate))
        {
            return $"{sunsetName} '{sunset}' is not a date in the format {DateFormat}";
        }

        if (!string.IsNullOrEmpty(since) && !string.IsNullOrEmpty(sunset) && sunsetDate <= sinceDate)
        {
            return $"{sunsetName} {sunset} must be after {sinceName} {since}";
        }

        return null;
    }

    private static string? ValidateUrl(string name, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return $"{name} '{value}' is not an absolute http(s) URL";
        }

        // Kestrel writes header values as ASCII and throws on anything else when the response starts — that would fail
        // every call instead of the start. Uri.TryCreate accepts IRIs (non-ASCII), so check the characters explicitly:
        // printable ASCII only, without the Link header delimiters.
        if (value.Any(c => c is <= ' ' or > '~' or '<' or '>' or '"'))
        {
            return $"{name} '{value}' contains a character that is not allowed in a Link header";
        }

        return null;
    }

    internal static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex WaveNamePattern();
}
