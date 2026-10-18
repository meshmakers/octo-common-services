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

    /// <summary>
    ///     Files of a running native archive dump or restore (CrateDB <c>COPY TO</c> / <c>COPY FROM</c>), one
    ///     nested folder per run: <c>&lt;instancePrefix&gt;/copy-staging/&lt;tenantId&gt;/&lt;runId&gt;/…</c> (see
    ///     <see cref="ArtifactKeyBuilder.BuildNested" />). A run deletes its files at the end; the retention of about
    ///     one day (app-side and as a lifecycle rule of the store) is the backstop for a crashed run.
    /// </summary>
    public const string CopyStaging = "copy-staging";
}
