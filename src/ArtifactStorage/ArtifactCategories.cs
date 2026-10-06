namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Well-known artifact categories, the second segment of a key built by <see cref="ArtifactKeyBuilder" />.
///     Lifecycle rules of the storage backend are defined per category prefix.
/// </summary>
public static class ArtifactCategories
{
    /// <summary>Dumps taken before a writing secret sweep (encrypted, retention about 7 days).</summary>
    public const string Presweep = "presweep";

    /// <summary>Tenant dumps created for download (retention about one day).</summary>
    public const string TenantDumps = "tenant-dumps";

    /// <summary>Uploaded files staged for a restore (retention about one day).</summary>
    public const string RestoreStaging = "restore-staging";
}
