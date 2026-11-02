using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     AB#8079: the wire form of a method result. A handler returns the generated <c>{Type}{Method}Result</c> record, a
///     wrapper with one <c>Value</c> property; <c>resultJson</c> on the wire is the bare camelCase JSON of that value (a
///     string result is <c>"abc"</c>, an int <c>42</c>, a record <c>{"name":...}</c>), never the wrapper
///     <c>{"value": ...}</c>. Every host (bus, in-process) serializes through this class, so all of them emit the same form.
/// </summary>
internal static class CkMethodResultShape
{
    /// <summary>The name of the result property of the generated <c>{Type}{Method}Result</c> record.</summary>
    public const string ValuePropertyName = "Value";

    private static readonly ConcurrentDictionary<Type, JsonPropertyInfo> ValueProperties = new();

    /// <summary>
    ///     The <c>Value</c> property of <paramref name="resultType" /> as <see cref="CkMethodJson.Options" /> serializes
    ///     it (its JSON name and converter). Throws <see cref="InvalidOperationException" /> when the type has no public
    ///     readable <c>Value</c> property, so a handler with a hand-written result type fails at registration, not at the
    ///     first call.
    /// </summary>
    public static JsonPropertyInfo ValueProperty(Type resultType) =>
        ValueProperties.GetOrAdd(resultType, static type =>
        {
            var typeInfo = CkMethodJson.Options.GetTypeInfo(type);
            return typeInfo.Properties.FirstOrDefault(p =>
                       p.AttributeProvider is PropertyInfo { Name: ValuePropertyName } property &&
                       property.GetIndexParameters().Length == 0 && p.Get != null)
                   ?? throw new InvalidOperationException(
                       $"The result type {type.Name} has no public '{ValuePropertyName}' property. A handler returns " +
                       "the generated {Type}{Method}Result record; its Value is the result on the wire.");
        });

    /// <summary>
    ///     The bare JSON of <c>result.Value</c>, or <c>null</c> when the result or its value is null (the response then
    ///     carries no <c>resultJson</c>). Uses the property's JSON contract, so a converter on <c>Value</c> applies as before.
    /// </summary>
    public static string? Serialize<TResult>(TResult? result) where TResult : class
    {
        var property = ValueProperty(typeof(TResult));
        if (result is null)
        {
            return null;
        }

        using var document = JsonSerializer.SerializeToDocument(result, CkMethodJson.Options);
        if (!document.RootElement.TryGetProperty(property.Name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.GetRawText();
    }
}
