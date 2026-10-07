using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Content type and custom properties stored together with an artifact.
/// </summary>
/// <remarks>
///     Property names and values are restricted to what every provider can round-trip unchanged:
///     names are lower-case ASCII letters, digits and <c>_</c> and start with a letter (valid as S3 user
///     metadata and as Azure Blob metadata, which must be C# identifiers); values are printable ASCII.
///     Never store secrets in metadata — it is not encrypted by the application and is visible to anyone
///     who can list the store.
/// </remarks>
public sealed partial class ArtifactMetadata
{
    /// <summary>
    ///     Content type every provider stores and reports when none is given, so that all providers report the
    ///     same value.
    /// </summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>Maximum length of a property name.</summary>
    public const int MaxPropertyNameLength = 64;

    /// <summary>Maximum total size (names plus values) of all properties, below the S3 limit of 2 KB.</summary>
    public const int MaxTotalPropertySize = 2000;

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    ///     Creates metadata.
    /// </summary>
    /// <param name="contentType">MIME type, e.g. <c>application/octet-stream</c>; <c>null</c> for none.</param>
    /// <param name="properties">Custom properties; <c>null</c> for none.</param>
    /// <exception cref="ArgumentException">A property name or value violates the rules above.</exception>
    public ArtifactMetadata(string? contentType = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (contentType != null && (contentType.Length == 0 || !IsPrintableAscii(contentType)))
        {
            throw new ArgumentException("The content type must be non-empty printable ASCII.", nameof(contentType));
        }

        ContentType = contentType;
        if (properties == null || properties.Count == 0)
        {
            Properties = EmptyProperties;
            return;
        }

        var total = 0;
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in properties)
        {
            if (name.Length > MaxPropertyNameLength || !PropertyNameRegex().IsMatch(name))
            {
                throw new ArgumentException(
                    $"Invalid metadata property name '{name}': use lower-case letters, digits and '_', starting with a letter, at most {MaxPropertyNameLength} characters.",
                    nameof(properties));
            }

            if (value == null || !IsPrintableAscii(value))
            {
                throw new ArgumentException($"The value of metadata property '{name}' must be printable ASCII.",
                    nameof(properties));
            }

            total += name.Length + value.Length;
            copy[name] = value;
        }

        if (total > MaxTotalPropertySize)
        {
            throw new ArgumentException(
                $"Metadata properties exceed {MaxTotalPropertySize} characters in total.", nameof(properties));
        }

        Properties = new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Metadata without content type and properties.</summary>
    public static ArtifactMetadata None { get; } = new();

    /// <summary>
    ///     MIME type of the content, or <c>null</c> when none was given on write (it is then stored as
    ///     <see cref="DefaultContentType" />, which is what reads report).
    /// </summary>
    public string? ContentType { get; }

    /// <summary>The content type to store: <see cref="ContentType" /> or <see cref="DefaultContentType" />.</summary>
    public string EffectiveContentType => ContentType ?? DefaultContentType;

    /// <summary>Custom properties (never <c>null</c>).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyNameRegex();
}
