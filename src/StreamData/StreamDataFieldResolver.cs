namespace Meshmakers.Octo.Services.StreamData;

/// <summary>
/// Classification of a stream data field.
/// </summary>
public enum StreamDataFieldCategory
{
    /// <summary>A built-in default field (RtId, Timestamp, etc.)</summary>
    Default,

    /// <summary>A CK model data stream attribute</summary>
    DataStream,

    /// <summary>Field not recognized as default or data stream</summary>
    Unknown
}

/// <summary>
/// Result of resolving a stream data field name.
/// </summary>
/// <param name="Category">Whether the field is a default, data stream, or unknown field</param>
/// <param name="CrateDbName">The canonical PascalCase name for CrateDB queries</param>
/// <param name="GraphQlAlias">The camelCase alias used in GraphQL responses</param>
/// <param name="IsDataField">True if the field is stored in the dynamic data column</param>
public record ResolvedField(
    StreamDataFieldCategory Category,
    string CrateDbName,
    string GraphQlAlias,
    bool IsDataField);

/// <summary>
/// Central resolver for stream data field names. Maps input field names (in any casing)
/// to their canonical CrateDB (PascalCase) and GraphQL (camelCase) representations.
/// </summary>
public class StreamDataFieldResolver
{
    private readonly Dictionary<string, ResolvedField> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new resolver with the given data stream attribute names.
    /// Default fields are always registered from <see cref="Constants.DefaultStreamDataFields"/>.
    /// </summary>
    /// <param name="dataStreamAttributeNames">PascalCase attribute names from the CK model</param>
    public StreamDataFieldResolver(IEnumerable<string> dataStreamAttributeNames)
    {
        // Register default fields from Constants (single source of truth)
        foreach (var defaultField in Constants.DefaultStreamDataFields)
        {
            _fields[defaultField] = new ResolvedField(
                StreamDataFieldCategory.Default,
                defaultField,
                ToCamelCase(defaultField),
                IsDataField: false);
        }

        // Register data stream attributes
        foreach (var attrName in dataStreamAttributeNames)
        {
            // Don't overwrite defaults if a data attribute happens to share the name
            if (!Constants.IsDefaultField(attrName))
            {
                _fields[attrName] = new ResolvedField(
                    StreamDataFieldCategory.DataStream,
                    attrName,
                    ToCamelCase(attrName),
                    IsDataField: true);
            }
        }
    }

    /// <summary>
    /// Creates a resolver with no data stream attributes (only default fields).
    /// </summary>
    public StreamDataFieldResolver() : this([])
    {
    }

    /// <summary>
    /// Resolves a field name to its canonical representation.
    /// Returns null if the field is not recognized as either a default or data stream field.
    /// </summary>
    public ResolvedField? Resolve(string input)
    {
        return _fields.GetValueOrDefault(input);
    }

    /// <summary>
    /// Resolves a field name, falling back to PascalCase for unknown fields.
    /// </summary>
    public ResolvedField ResolveOrFallback(string input)
    {
        return Resolve(input) ?? new ResolvedField(
            StreamDataFieldCategory.Unknown,
            ToPascalCase(input),
            ToCamelCase(input),
            IsDataField: false);
    }

    private static string ToCamelCase(string s)
    {
        return string.IsNullOrWhiteSpace(s)
            ? string.Empty
            : $"{char.ToLowerInvariant(s[0])}{s.Substring(1)}";
    }

    private static string ToPascalCase(string s)
    {
        return string.IsNullOrWhiteSpace(s)
            ? string.Empty
            : $"{char.ToUpperInvariant(s[0])}{s.Substring(1)}";
    }
}
