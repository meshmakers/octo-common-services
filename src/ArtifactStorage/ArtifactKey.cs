namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Validation rules for artifact keys and list prefixes, shared by all providers so that a key that is
///     valid for one provider is valid for all of them.
/// </summary>
/// <remarks>
///     A key is one or more segments separated by <c>/</c>. Each segment consists of ASCII letters, digits,
///     <c>-</c>, <c>_</c> and <c>.</c>, does not start with <c>.</c> (which also rules out <c>.</c> and
///     <c>..</c>) and is at most <see cref="MaxSegmentLength" /> characters long. Keys have no leading or
///     trailing <c>/</c> and no empty segments, and are at most <see cref="MaxKeyLength" /> characters long.
///     Keys are case-sensitive on S3 and Azure; the file system provider may be case-insensitive, so never
///     rely on two keys differing only in case.
/// </remarks>
public static class ArtifactKey
{
    /// <summary>Maximum key length (S3 allows 1024 bytes).</summary>
    public const int MaxKeyLength = 1024;

    /// <summary>Maximum segment length (common file system limit).</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>Separator between key segments.</summary>
    public const char Separator = '/';

    /// <summary>Returns <c>true</c> when <paramref name="key" /> is a valid artifact key.</summary>
    public static bool IsValid(string? key)
    {
        return key is { Length: > 0 and <= MaxKeyLength } && AreSegmentsValid(key, allowTrailingSeparator: false);
    }

    /// <summary>Throws <see cref="ArgumentException" /> when <paramref name="key" /> is not a valid artifact key.</summary>
    public static void Validate(string? key, string paramName = "key")
    {
        if (!IsValid(key))
        {
            throw new ArgumentException(
                $"Invalid artifact key '{key}': use '/'-separated segments of ASCII letters, digits, '-', '_' and '.', not starting with '.', at most {MaxKeyLength} characters.",
                paramName);
        }
    }

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="prefix" /> is a valid list prefix: empty, or a key that may end
    ///     with <c>/</c> or with a partial segment.
    /// </summary>
    public static bool IsValidPrefix(string? prefix)
    {
        if (prefix == null)
        {
            return false;
        }

        return prefix.Length == 0 ||
               (prefix.Length <= MaxKeyLength && AreSegmentsValid(prefix, allowTrailingSeparator: true));
    }

    /// <summary>Throws <see cref="ArgumentException" /> when <paramref name="prefix" /> is not a valid list prefix.</summary>
    public static void ValidatePrefix(string? prefix, string paramName = "prefix")
    {
        if (!IsValidPrefix(prefix))
        {
            throw new ArgumentException($"Invalid artifact key prefix '{prefix}'.", paramName);
        }
    }

    /// <summary>Returns <c>true</c> when <paramref name="segment" /> is a valid single key segment.</summary>
    public static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.Length > MaxSegmentLength || segment[0] == '.')
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!IsSegmentChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreSegmentsValid(string value, bool allowTrailingSeparator)
    {
        var segments = value.Split(Separator);
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            if (isLast && allowTrailingSeparator && segments[i].Length == 0 && segments.Length > 1)
            {
                continue;
            }

            if (!IsValidSegment(segments[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSegmentChar(char c)
    {
        return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
    }
}
