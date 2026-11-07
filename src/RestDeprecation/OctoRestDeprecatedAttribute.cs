using System.Globalization;

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
///         The values are set in code at GA of the migration wave; there is no configuration option. All three are
///         required: <see cref="Since" /> and <see cref="Sunset" /> as <c>yyyy-MM-dd</c> (UTC midnight),
///         <see cref="Sunset" /> after <see cref="Since" />, and <see cref="Link" /> an absolute http(s) URL. An invalid
///         value fails the service at startup with a message naming the action.
///     </para>
///     <para>
///         Optional: <see cref="Successor" />, an absolute http(s) URL of the successor operation. When set, the responses
///         carry the further Link value <c>&lt;Successor&gt;; rel="successor-version"</c> (RFC 5829); it is validated at
///         startup like <see cref="Link" />.
///     </para>
///     <para>See <c>docs/rest-deprecation.md</c> in octo-common-services.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OctoRestDeprecatedAttribute : Attribute
{
    internal const string DateFormat = "yyyy-MM-dd";

    private Values? _values;

    /// <summary>Day (UTC, <c>yyyy-MM-dd</c>) the operation became deprecated, normally GA of the replacing method.</summary>
    public string Since { get; set; } = string.Empty;

    /// <summary>Day (UTC, <c>yyyy-MM-dd</c>) after which the operation may be removed; must be after <see cref="Since" />.</summary>
    public string Sunset { get; set; } = string.Empty;

    /// <summary>Absolute http(s) URL of the documentation of the replacing operation (for example the CK method).</summary>
    public string Link { get; set; } = string.Empty;

    /// <summary>
    ///     Optional absolute http(s) URL of the successor operation; adds <c>Link: &lt;url&gt;; rel="successor-version"</c>
    ///     (RFC 5829) to every response.
    /// </summary>
    public string? Successor { get; set; }

    /// <summary>The <c>Deprecation</c> header value (RFC 9745 structured date, e.g. <c>@1798761600</c>).</summary>
    internal string DeprecationHeader => GetValues().DeprecationHeader;

    /// <summary>The <c>Sunset</c> header value (RFC 8594, IMF-fixdate).</summary>
    internal string SunsetHeader => GetValues().SunsetHeader;

    /// <summary>The <c>Link</c> header value (RFC 9745 relation type <c>deprecation</c>).</summary>
    internal string LinkHeader => GetValues().LinkHeader;

    /// <summary>The additional <c>Link</c> value (RFC 5829 relation type <c>successor-version</c>); <c>null</c> without <see cref="Successor" />.</summary>
    internal string? SuccessorLinkHeader => GetValues().SuccessorLinkHeader;

    /// <summary>
    ///     Checks the values; returns <c>null</c> when they are valid, otherwise the reason.
    /// </summary>
    internal string? Validate()
    {
        if (!TryParseDate(Since, out var since))
        {
            return $"Since '{Since}' is not a date in the format {DateFormat}";
        }

        if (!TryParseDate(Sunset, out var sunset))
        {
            return $"Sunset '{Sunset}' is not a date in the format {DateFormat}";
        }

        if (sunset <= since)
        {
            return $"Sunset {Sunset} must be after Since {Since}";
        }

        return ValidateUrl(nameof(Link), Link) ??
               (string.IsNullOrEmpty(Successor) ? null : ValidateUrl(nameof(Successor), Successor));
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

    private Values GetValues()
    {
        // Endpoint metadata instances live as long as the endpoint, so the header values are computed once.
        return _values ??= CreateValues();
    }

    private Values CreateValues()
    {
        var error = Validate();
        if (error != null)
        {
            throw new InvalidOperationException($"[OctoRestDeprecated] is invalid: {error}");
        }

        TryParseDate(Since, out var since);
        TryParseDate(Sunset, out var sunset);
        return new Values(
            "@" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            sunset.ToString("r", CultureInfo.InvariantCulture),
            $"<{Link}>; rel=\"deprecation\"; type=\"text/html\"",
            string.IsNullOrEmpty(Successor) ? null : $"<{Successor}>; rel=\"successor-version\"");
    }

    private static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        return DateTimeOffset.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }

    private sealed record Values(
        string DeprecationHeader,
        string SunsetHeader,
        string LinkHeader,
        string? SuccessorLinkHeader);
}
