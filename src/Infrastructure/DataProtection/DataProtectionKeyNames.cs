namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>
///     Naming rule of the shared Data Protection key store: every service keeps its own key ring in the same store, told
///     apart by the friendly name of the stored element.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>A service key ring stores its elements as <c>&lt;applicationName&gt;/&lt;friendlyName&gt;</c>
///         (for example <c>OctoAssetRepositoryServices/key-0f8c…</c>).</item>
///         <item>The identity service keeps its existing key ring without a prefix (<c>key-0f8c…</c>); a name without
///         a <see cref="Separator" /> belongs to identity and is never part of a service key ring.</item>
///     </list>
///     An application name contains no <see cref="Separator" />, so one ring can never match the elements of another one
///     (<c>Octo/</c> does not match <c>OctoX/…</c>).
/// </remarks>
public static class DataProtectionKeyNames
{
    /// <summary>Separates the application name from the element name.</summary>
    public const char Separator = '/';

    /// <summary>Longest accepted application name.</summary>
    public const int MaxApplicationNameLength = 100;

    /// <summary>
    ///     Throws when <paramref name="applicationName" /> cannot be used as a key ring name: empty, longer than
    ///     <see cref="MaxApplicationNameLength" />, or containing whitespace or the <see cref="Separator" />.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not valid</exception>
    public static void ValidateApplicationName(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new ArgumentException("The Data Protection application name must not be empty.",
                nameof(applicationName));
        }

        if (applicationName.Length > MaxApplicationNameLength ||
            applicationName.Any(c => c == Separator || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException(
                $"The Data Protection application name '{applicationName}' must have at most " +
                $"{MaxApplicationNameLength} characters and contain no whitespace and no '{Separator}'.",
                nameof(applicationName));
        }
    }

    /// <summary>The stored name of element <paramref name="friendlyName" /> of the key ring of an application.</summary>
    public static string ToStoredName(string applicationName, string friendlyName)
    {
        ValidateApplicationName(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        return $"{applicationName}{Separator}{friendlyName}";
    }

    /// <summary>True when the stored element belongs to the key ring of <paramref name="applicationName" />.</summary>
    public static bool BelongsTo(string applicationName, string? storedName)
    {
        return storedName != null && storedName.StartsWith(applicationName + Separator, StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when the stored element belongs to the identity key ring, which has no application prefix.
    /// </summary>
    public static bool IsUnprefixed(string? storedName)
    {
        return storedName != null && !storedName.Contains(Separator, StringComparison.Ordinal);
    }
}
