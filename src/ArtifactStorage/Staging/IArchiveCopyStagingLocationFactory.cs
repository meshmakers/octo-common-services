using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Services.ArtifactStorage.Staging;

/// <summary>
///     Creates the <see cref="ArchiveCopyStagingLocation" /> of one native archive dump or restore run from the artifact
///     store settings (AB#5967): the location of the files as the CrateDB nodes see it, below the category
///     <see cref="ArtifactCategories.CopyStaging" />.
/// </summary>
/// <remarks>
///     <para>
///         The files of a run live under <c>&lt;instancePrefix&gt;/copy-staging/&lt;tenantId&gt;/&lt;runId&gt;/</c> in the store.
///         The service reads, writes and deletes them through <see cref="IArtifactStore" /> with the keys of
///         <see cref="ArtifactKeyBuilder.BuildNested" />; CrateDB addresses the same objects through the location this
///         factory returns. The service never has to know the credential: <see cref="ArchiveCopyStagingLocation" />
///         shows it only through its <c>Reveal…</c> methods.
///     </para>
///     <para>
///         A store CrateDB cannot reach (the file system provider) or that cannot issue a credential (Azure without an
///         account key) is <b>not supported</b>: <see cref="IsSupported" /> is <c>false</c>, <see cref="UnsupportedReason" />
///         says why, and callers run the dump and restore as format 1 (rows through the service).
///     </para>
/// </remarks>
public interface IArchiveCopyStagingLocationFactory
{
    /// <summary>Whether the configured artifact store can serve as the staging area of a native archive copy.</summary>
    bool IsSupported { get; }

    /// <summary>Why the store is not supported (settings named, no values); <c>null</c> when it is supported.</summary>
    string? UnsupportedReason { get; }

    /// <summary>
    ///     Creates the location of one run.
    /// </summary>
    /// <param name="tenantId">The tenant id (lower-cased in the location).</param>
    /// <param name="runId">The run folder; one key segment.</param>
    /// <param name="access">Whether CrateDB writes (dump) or reads (restore); decides the permissions of the credential.</param>
    /// <param name="subPath">
    ///     Optional <c>/</c>-separated folder below the run folder, e.g. <c>&lt;rtId&gt;/data/&lt;slice&gt;</c>
    ///     (see <see cref="ArchiveCopyStagingLocation.WithSubPath" />).
    /// </param>
    /// <exception cref="ArchiveCopyStagingNotSupportedException">The store is not supported (see <see cref="IsSupported" />).</exception>
    /// <exception cref="ArgumentException">The tenant id, run id or sub path is invalid.</exception>
    ArchiveCopyStagingLocation Create(string tenantId, string runId, ArchiveCopyStagingAccess access,
        string? subPath = null);
}

/// <summary>
///     The configured artifact store cannot serve as the staging area of a native archive copy. The message names the
///     setting or the missing capability and never contains a credential.
/// </summary>
public sealed class ArchiveCopyStagingNotSupportedException : NotSupportedException
{
    /// <summary>Creates the exception with the reason (settings named, no values).</summary>
    public ArchiveCopyStagingNotSupportedException(string reason)
        : base(reason)
    {
    }
}
