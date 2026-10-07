using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Builds and parses keys of the layout <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;file&gt;</c>.
/// </summary>
/// <remarks>
///     The instance prefix keeps several OctoMesh instances that share one bucket or container apart
///     (e.g. test-2 main, dev and release). Tenant ids are lower-cased and validated with the same rules as
///     tenant creation (at most 64 ASCII letters, digits, <c>-</c> or <c>_</c>).
/// </remarks>
public sealed class ArtifactKeyBuilder
{
    /// <summary>Maximum tenant id length, kept in step with tenant creation.</summary>
    public const int MaxTenantIdLength = 64;

    /// <summary>
    ///     Creates a builder for <paramref name="instancePrefix" />.
    /// </summary>
    /// <param name="instancePrefix">One or more key segments, e.g. <c>main</c> or <c>test-2/main</c>.</param>
    /// <exception cref="ArgumentException">The instance prefix is not a valid key.</exception>
    public ArtifactKeyBuilder(string instancePrefix)
    {
        if (!ArtifactKey.IsValid(instancePrefix))
        {
            throw new ArgumentException($"Invalid artifact instance prefix '{instancePrefix}'.",
                nameof(instancePrefix));
        }

        InstancePrefix = instancePrefix;
    }

    /// <summary>
    ///     Creates a builder from the configured <see cref="ArtifactStorageOptions.InstancePrefix" />.
    /// </summary>
    public ArtifactKeyBuilder(IOptions<ArtifactStorageOptions> options)
        : this(options.Value.InstancePrefix)
    {
    }

    /// <summary>The instance prefix (without trailing <c>/</c>).</summary>
    public string InstancePrefix { get; }

    /// <summary>
    ///     Returns <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;fileName&gt;</c>.
    /// </summary>
    /// <exception cref="ArgumentException">A part is invalid.</exception>
    public string Build(string category, string tenantId, string fileName)
    {
        ValidateCategory(category);
        var tenant = NormalizeTenantId(tenantId);
        if (!ArtifactKey.IsValidSegment(fileName))
        {
            throw new ArgumentException($"Invalid artifact file name '{fileName}'.", nameof(fileName));
        }

        var key = $"{InstancePrefix}/{category}/{tenant}/{fileName}";
        ArtifactKey.Validate(key, nameof(fileName));
        return key;
    }

    /// <summary>Returns the list prefix <c>&lt;instancePrefix&gt;/&lt;category&gt;/</c>.</summary>
    public string CategoryPrefix(string category)
    {
        ValidateCategory(category);
        return $"{InstancePrefix}/{category}/";
    }

    /// <summary>Returns the list prefix <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/</c>.</summary>
    public string TenantPrefix(string category, string tenantId)
    {
        ValidateCategory(category);
        return $"{InstancePrefix}/{category}/{NormalizeTenantId(tenantId)}/";
    }

    /// <summary>
    ///     Parses a key built by this builder (same instance prefix).
    /// </summary>
    /// <returns><c>true</c> when the key has the expected layout.</returns>
    public bool TryParse(string? key, out ArtifactKeyParts? parts)
    {
        parts = null;
        if (!ArtifactKey.IsValid(key) || !key!.StartsWith(InstancePrefix + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = key[(InstancePrefix.Length + 1)..].Split(ArtifactKey.Separator);
        if (rest.Length != 3 || !IsValidTenantId(rest[1]) || !string.Equals(rest[1], rest[1].ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return false;
        }

        parts = new ArtifactKeyParts(rest[0], rest[1], rest[2]);
        return true;
    }

    /// <summary>
    ///     Validates and lower-cases a tenant id.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant id is invalid.</exception>
    public static string NormalizeTenantId(string tenantId)
    {
        if (!IsValidTenantId(tenantId))
        {
            throw new ArgumentException(
                $"Invalid tenant id '{tenantId}': at most {MaxTenantIdLength} ASCII letters, digits, '-' or '_'.",
                nameof(tenantId));
        }

        return tenantId.ToLowerInvariant();
    }

    private static bool IsValidTenantId(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId) || tenantId.Length > MaxTenantIdLength)
        {
            return false;
        }

        foreach (var c in tenantId)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateCategory(string category)
    {
        if (!ArtifactKey.IsValidSegment(category))
        {
            throw new ArgumentException($"Invalid artifact category '{category}'.", nameof(category));
        }
    }
}
