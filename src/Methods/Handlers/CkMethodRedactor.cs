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
        new(string.IsNullOrEmpty(request.CallerAccessToken) ? [] : [request.CallerAccessToken]);

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

        // longest first, so a secret that contains another one is masked completely
        return new CkMethodRedactor(secrets.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).ToArray());
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
