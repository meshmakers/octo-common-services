using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Tells the invocation pipeline which parameters of a CK method are sensitive (<c>sensitive: true</c> in the CK
///     model), so that it can hand the filters a redacted view of the request (AB#6914, F3.4).
/// </summary>
/// <remarks>
///     The default implementation reads the definition from the same source as the authorization filter
///     (<see cref="ICkMethodAuthorizationDefinitionSource" />, by default the per-tenant CK cache). When no source is
///     available the pipeline is <b>fail-closed</b>: every parameter value is treated as sensitive.
/// </remarks>
public interface ICkMethodSensitivityResolver
{
    /// <summary>
    ///     The names (as declared in the CK model, matched case-insensitively) of the sensitive parameters of the method,
    ///     or <c>null</c> when the method is not known (the caller then treats <b>all</b> parameters as sensitive). Never
    ///     throws.
    /// </summary>
    /// <param name="tenantId">Tenant of the request.</param>
    /// <param name="ckTypeId">Concrete CK type of the target (<c>Model/Type</c>).</param>
    /// <param name="qualifiedMethodId">Qualified id of the method on its declaring element.</param>
    IReadOnlySet<string>? GetSensitiveParameters(string tenantId, string ckTypeId, string qualifiedMethodId);
}

internal sealed class CkMethodSensitivityResolver(ICkMethodAuthorizationDefinitionSource? source)
    : ICkMethodSensitivityResolver
{
    public IReadOnlySet<string>? GetSensitiveParameters(string tenantId, string ckTypeId, string qualifiedMethodId)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            var definition = source.Find(tenantId, ckTypeId, qualifiedMethodId);
            return definition == null
                ? null
                : (definition.Parameters ?? []).Where(p => p.Sensitive).Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // a fault in the source must not reveal anything: unknown, so everything is redacted
            return null;
        }
    }
}

/// <summary>Redaction of the request view that the pipeline hands to filters (the raw request goes to the transport only).</summary>
internal static class CkMethodRequestRedaction
{
    /// <summary>The raw request with sensitive parameter values replaced by the placeholder and the caller token removed.</summary>
    /// <param name="raw">The request as the caller built it.</param>
    /// <param name="sensitive">Sensitive parameter names; <c>null</c> redacts every value.</param>
    public static CkMethodRequest CreateView(CkMethodRequest raw, IReadOnlySet<string>? sensitive) =>
        raw with { ParametersJson = RedactParameters(raw.ParametersJson, sensitive), CallerAccessToken = null };

    /// <summary>
    ///     Maps the request a filter passed on back to the one the transport must get (AB#6914, AB#7016). A filter can
    ///     only have seen the view, so the absent token is replaced by the raw one, and for <c>ParametersJson</c>:
    ///     <list type="bullet">
    ///         <item>unchanged: the raw parameters;</item>
    ///         <item>
    ///             edited: <b>merged per member</b>. A sensitive member that is still the placeholder is restored from the
    ///             raw request, so an edit of a non-sensitive member keeps the real secret. A sensitive member the filter
    ///             gave another value (or removed) is an explicit edit and is honoured; everything else the filter
    ///             changed is kept;
    ///         </item>
    ///         <item>
    ///             a placeholder for a sensitive member the raw request does not carry cannot be restored: the call is
    ///             refused (<paramref name="unresolvedMember" />), the handler never gets <c>"***"</c> as a value.
    ///         </item>
    ///     </list>
    /// </summary>
    /// <param name="raw">The request as the caller built it.</param>
    /// <param name="view">The redacted view the filters started from.</param>
    /// <param name="passed">The request the last filter passed on.</param>
    /// <param name="sensitive">Sensitive parameter names; <c>null</c> treats every member as sensitive.</param>
    /// <param name="restored">The request for the transport.</param>
    /// <param name="unresolvedMember">The offending member name when the result is <c>false</c>.</param>
    public static bool TryRestore(CkMethodRequest raw, CkMethodRequest view, CkMethodRequest passed,
        IReadOnlySet<string>? sensitive, out CkMethodRequest restored, out string? unresolvedMember)
    {
        unresolvedMember = null;
        var parameters = passed.ParametersJson;
        if (string.Equals(parameters, view.ParametersJson, StringComparison.Ordinal))
        {
            parameters = raw.ParametersJson;
        }
        else if (!TryMergePlaceholders(raw.ParametersJson, parameters, sensitive, out parameters, out unresolvedMember))
        {
            restored = passed;
            return false;
        }

        restored = passed with
        {
            ParametersJson = parameters,
            CallerAccessToken = passed.CallerAccessToken ?? raw.CallerAccessToken
        };
        return true;
    }

    private static bool TryMergePlaceholders(string rawJson, string passedJson, IReadOnlySet<string>? sensitive,
        out string merged, out string? unresolvedMember)
    {
        merged = passedJson;
        unresolvedMember = null;
        JsonDocument passed;
        try
        {
            passed = JsonDocument.Parse(passedJson);
        }
        catch (JsonException)
        {
            return true; // not our business: the handler side answers METHOD_VALIDATION_FAILED
        }

        using (passed)
        {
            if (passed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            var placeholders = passed.RootElement.EnumerateObject()
                .Where(p => IsPlaceholder(p.Value) &&
                            (sensitive == null || sensitive.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (placeholders.Count == 0)
            {
                return true;
            }

            JsonDocument? raw = null;
            try
            {
                try
                {
                    raw = JsonDocument.Parse(rawJson);
                }
                catch (JsonException)
                {
                    // no raw members to restore from
                }

                var rawMembers = raw is { RootElement.ValueKind: JsonValueKind.Object }
                    ? raw.RootElement.EnumerateObject().ToList()
                    : [];

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var property in passed.RootElement.EnumerateObject())
                    {
                        if (!placeholders.Contains(property.Name))
                        {
                            property.WriteTo(writer);
                            continue;
                        }

                        var source = rawMembers.Where(m => string.Equals(m.Name, property.Name, StringComparison.Ordinal))
                            .Concat(rawMembers.Where(m =>
                                string.Equals(m.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                            .Cast<JsonProperty?>().FirstOrDefault();
                        if (source == null)
                        {
                            unresolvedMember = property.Name;
                            return false;
                        }

                        writer.WritePropertyName(property.Name);
                        source.Value.Value.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                merged = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                return true;
            }
            finally
            {
                raw?.Dispose();
            }
        }
    }

    private static bool IsPlaceholder(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.ValueEquals(CkMethodRedactor.Mask);

    private static string RedactParameters(string parametersJson, IReadOnlySet<string>? sensitive)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "{}";
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (sensitive == null || sensitive.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        writer.WriteString(property.Name, CkMethodRedactor.Mask);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // An unparsable payload may still hold a secret and is never shown.
            return "{}";
        }
    }
}
