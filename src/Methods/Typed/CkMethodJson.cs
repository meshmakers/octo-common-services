using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     The JSON options for method parameters and results on the wire (<c>parametersJson</c>, <c>resultJson</c>):
///     camelCase, case-insensitive reading, enums written as names (numbers are still accepted when reading).
///     Every transport (bus, in-process, pipeline) must use these options so that all of them produce and read the same JSON.
/// </summary>
/// <remarks>
///     Generated runtime records (<c>Rt{Record}Record</c>, AB#8707) bind with their CK attributes only: the bookkeeping members
///     of <see cref="RtTypeWithAttributes" /> (<c>attributes</c>, <c>ckRecordId</c>) are neither read nor written, and a
///     record or primitive array attribute (<see cref="IAttributeValueList{TValue}" />, an interface System.Text.Json cannot
///     create) is read as a list and stored in the record. So the generated <c>{Type}{Method}Parameters</c> of a model work as
///     handler parameter types, also when a Record parameter holds a record array.
/// </remarks>
public static class CkMethodJson
{
    /// <summary>The shared serializer options. Do not modify.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { CkRuntimeRecordContract.Apply } }
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
///     The JSON contract of generated runtime records (<see cref="RtTypeWithAttributes" /> subclasses) in method JSON (AB#8707).
/// </summary>
internal static class CkRuntimeRecordContract
{
    public static void Apply(JsonTypeInfo typeInfo)
    {
        // Only the generated subclasses: RtRecord itself binds through its JSON constructor (ckRecordId, attributes).
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !typeof(RtTypeWithAttributes).IsAssignableFrom(typeInfo.Type) ||
            typeInfo.Type == typeof(RtRecord))
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            var property = typeInfo.Properties[i];
            var member = property.AttributeProvider as System.Reflection.PropertyInfo;
            if (member?.DeclaringType is { } declaringType && IsBookkeepingType(declaringType))
            {
                // Attributes / CkRecordId: the attribute bag itself, never a CK attribute of the record.
                typeInfo.Properties.RemoveAt(i);
                continue;
            }

            if (member != null && ElementTypeOfAttributeValueList(property.PropertyType) is { } elementType)
            {
                typeInfo.Properties[i] = ListProperty(typeInfo, property, member, elementType);
            }
        }
    }

    private static bool IsBookkeepingType(Type type) =>
        type == typeof(RtTypeWithAttributes) || type == typeof(RtRecord);

    private static Type? ElementTypeOfAttributeValueList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAttributeValueList<>)
            ? type.GetGenericArguments()[0]
            : null;

    /// <summary>
    ///     Replaces an <see cref="IAttributeValueList{TValue}" /> property by an <see cref="IList{T}" /> one: System.Text.Json
    ///     reads it into a <see cref="List{T}" /> and writes the record's list as an array. The value is stored the way the
    ///     generated getter reads it back (records as <c>List&lt;RtRecord&gt;</c>, strings and numbers as <c>List&lt;T&gt;</c>).
    /// </summary>
    private static JsonPropertyInfo ListProperty(JsonTypeInfo typeInfo, JsonPropertyInfo original,
        System.Reflection.PropertyInfo member, Type elementType)
    {
        var listType = typeof(IList<>).MakeGenericType(elementType);
        var property = typeInfo.CreateJsonPropertyInfo(listType, original.Name);
        property.AttributeProvider = original.AttributeProvider;
        property.IsRequired = original.IsRequired;
        property.Order = original.Order;
        property.Get = original.Get;
        var attributeName = member.Name;
        var isRecord = typeof(RtRecord).IsAssignableFrom(elementType);
        property.Set = (target, value) =>
        {
            var record = (RtTypeWithAttributes)target;
            record.SetAttributeRawValue(attributeName, value switch
            {
                null => null,
                System.Collections.IEnumerable records when isRecord => records.Cast<RtRecord>().ToList(),
                _ => value
            });
        };
        return property;
    }
}
