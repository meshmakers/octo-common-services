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
    ///     Maps the request a filter passed on back to the one the transport must get. A filter can only have seen the
    ///     view, so an untouched redacted <c>ParametersJson</c> and an absent token are replaced by the raw values; anything
    ///     else the filter changed (deadline, idempotency key, even new parameters) is kept.
    /// </summary>
    public static CkMethodRequest Restore(CkMethodRequest raw, CkMethodRequest view, CkMethodRequest passed) =>
        passed with
        {
            ParametersJson = string.Equals(passed.ParametersJson, view.ParametersJson, StringComparison.Ordinal)
                ? raw.ParametersJson
                : passed.ParametersJson,
            CallerAccessToken = passed.CallerAccessToken ?? raw.CallerAccessToken
        };

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
                    if (sensitive == null || sensitive.Contains(property.Name))
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
