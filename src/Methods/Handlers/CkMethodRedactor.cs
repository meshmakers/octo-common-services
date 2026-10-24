using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Keeps sensitive method parameters (CK <c>sensitive: true</c>) and the caller token out of host logs and exception
///     messages, even when a handler puts them into an exception message.
/// </summary>
internal sealed class CkMethodRedactor
{
    public const string Mask = "***";

    private readonly string[] _secrets;

    private CkMethodRedactor(string[] secrets)
    {
        _secrets = secrets;
    }

    /// <summary>Masks the caller token only (used where no registration, and so no sensitive parameter list, is known).</summary>
    public static CkMethodRedactor ForToken(CkMethodRequest request) =>
        new(string.IsNullOrEmpty(request.CallerAccessToken)
            ? []
            : SecretForms(request.CallerAccessToken).Distinct(StringComparer.Ordinal)
                .OrderByDescending(s => s.Length).ToArray());

    public static CkMethodRedactor For(CkMethodRegistration registration, CkMethodRequest request) =>
        For(registration.SensitiveParameters, request);

    /// <summary>
    ///     Masks the caller token and the values of the named sensitive parameters. <paramref name="sensitive" /> =
    ///     <c>null</c> (method not known) masks every parameter value. A payload that cannot be parsed is masked as a
    ///     whole, so an exception message that quotes it stays clean.
    /// </summary>
    public static CkMethodRedactor For(IReadOnlySet<string>? sensitive, CkMethodRequest request)
    {
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(request.CallerAccessToken))
        {
            secrets.Add(request.CallerAccessToken);
        }

        if (sensitive == null || sensitive.Count > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(request.ParametersJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject()
                                 .Where(p => sensitive == null ||
                                             sensitive.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        CollectSecrets(property.Value, secrets);
                    }
                }
                else
                {
                    CollectSecrets(document.RootElement, secrets);
                }
            }
            catch (JsonException)
            {
                secrets.Add(request.ParametersJson);
            }
        }

        // Every secret is masked in its plain form and in the escaped forms a serializer or a URL writes
        // (AB#7015); longest first, so a secret that contains another one is masked completely.
        return new CkMethodRedactor(secrets.Where(s => s.Length > 0).SelectMany(SecretForms)
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray());
    }

    /// <summary>
    ///     A redactor that additionally masks the values of <paramref name="resultJson" />, the serialized result of a
    ///     sensitive method (AB#7007): every string, number and boolean in it, in the plain and escaped forms, and the
    ///     whole JSON text. Used where the host forwards the response and an exception may quote it.
    /// </summary>
    public CkMethodRedactor WithResult(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return this;
        }

        var secrets = new List<string> { resultJson };
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            CollectSecrets(document.RootElement, secrets);
        }
        catch (JsonException)
        {
            // not JSON: the whole text is masked
        }

        return new CkMethodRedactor(_secrets.Concat(secrets.Where(s => s.Length > 0).SelectMany(SecretForms))
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray());
    }

    /// <summary>
    ///     The texts under which a decoded secret can show up in a message: the plain value, its JSON-escaped forms
    ///     (System.Text.Json default and relaxed encoder, Newtonsoft.Json, and every combination of the optional
    ///     escapes: non-ASCII and HTML-sensitive characters as <c>\uXXXX</c> in either hex case, escaped slash, long
    ///     control-character form), the same once more for JSON embedded in a JSON string, and the percent-encoded
    ///     (URL) form.
    /// </summary>
    internal static IEnumerable<string> SecretForms(string secret)
    {
        yield return secret;

        var json = new HashSet<string>(StringComparer.Ordinal);
        for (var flags = 0; flags < 64; flags++)
        {
            var escaped = JsonEscape(secret, flags);
            if (json.Add(escaped) && escaped != secret)
            {
                yield return escaped;
                // JSON inside a JSON string value (a payload quoted by an exception that is serialized again)
                var twice = JsonEscape(escaped, 0);
                if (twice != escaped)
                {
                    yield return twice;
                }
            }
        }

        foreach (var options in new[] { JsonDefaultEscaping, JsonRelaxedEscaping })
        {
            var text = JsonSerializer.Serialize(secret, options);
            yield return text[1..^1];
        }

        var url = Uri.EscapeDataString(secret);
        if (url != secret)
        {
            yield return url;
            yield return url.Replace("%20", "+", StringComparison.Ordinal);
            yield return url.ToLowerInvariant();
        }
    }

    private static readonly JsonSerializerOptions JsonDefaultEscaping = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    private static readonly JsonSerializerOptions JsonRelaxedEscaping = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const int EscapeNonAscii = 1;
    private const int EscapeHtml = 2;
    private const int EscapeSlash = 4;
    private const int LowerHex = 8;
    private const int LongControl = 16;
    private const int QuoteAsUnicode = 32;

    private static string JsonEscape(string value, int flags)
    {
        var hex = (flags & LowerHex) != 0 ? "x4" : "X4";
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"' when (flags & QuoteAsUnicode) != 0:
                    builder.Append("\\u").Append(((int)c).ToString(hex));
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '/' when (flags & EscapeSlash) != 0:
                    builder.Append("\\/");
                    break;
                case '\b' when (flags & LongControl) == 0:
                    builder.Append("\\b");
                    break;
                case '\f' when (flags & LongControl) == 0:
                    builder.Append("\\f");
                    break;
                case '\n' when (flags & LongControl) == 0:
                    builder.Append("\\n");
                    break;
                case '\r' when (flags & LongControl) == 0:
                    builder.Append("\\r");
                    break;
                case '\t' when (flags & LongControl) == 0:
                    builder.Append("\\t");
                    break;
                case < ' ':
                case '\u007f':
                    builder.Append("\\u").Append(((int)c).ToString(hex));
                    break;
                case '<' or '>' or '&' or '\'' or '+' or '`' when (flags & EscapeHtml) != 0:
                    builder.Append("\\u").Append(((int)c).ToString(hex));
                    break;
                case > '\u007f' when (flags & EscapeNonAscii) != 0:
                    builder.Append("\\u").Append(((int)c).ToString(hex));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void CollectSecrets(JsonElement value, List<string> secrets)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                secrets.Add(value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    CollectSecrets(item, secrets);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    CollectSecrets(property.Value, secrets);
                }

                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                secrets.Add(value.GetRawText());
                break;
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return _secrets.Aggregate(text, (current, secret) => current.Replace(secret, Mask, StringComparison.Ordinal));
    }

    /// <summary>The exception to log: same type name, message and stack trace with the secrets masked.</summary>
    public Exception Redact(Exception exception) => new RedactedException(exception, this);

    private sealed class RedactedException(Exception original, CkMethodRedactor redactor)
        : Exception(redactor.Redact(original.Message))
    {
        private readonly string _text = redactor.Redact(original.ToString());

        public override string? StackTrace => redactor.Redact(original.StackTrace);

        public override string ToString() => _text;
    }
}
