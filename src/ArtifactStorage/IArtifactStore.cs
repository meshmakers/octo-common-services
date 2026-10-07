namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Stores operational artifacts (pre-sweep dumps, tenant dumps, restore staging files) in a
///     provider-neutral way: the local file system, an S3-compatible bucket or an Azure Blob container.
/// </summary>
/// <remarks>
///     <para>
///         Keys are relative, <c>/</c>-separated paths (see <see cref="ArtifactKey" /> for the rules and
///         <see cref="ArtifactKeyBuilder" /> for the <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;file&gt;</c>
///         layout). Writing a key that already exists replaces the artifact.
///     </para>
///     <para>
///         Implementations stream content in both directions and never buffer a whole artifact in memory.
///         They do not log artifact content or metadata values.
///     </para>
/// </remarks>
public interface IArtifactStore
{
    /// <summary>
    ///     Short name of the provider (<c>FileSystem</c>, <c>S3</c>, <c>AzureBlob</c>), for logs and health output.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    ///     Writes <paramref name="content" /> to <paramref name="key" />, replacing an existing artifact.
    ///     The stream is read to its end; it does not need to be seekable.
    /// </summary>
    /// <param name="key">The artifact key.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="metadata">Content type and custom properties stored with the artifact.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The key or the metadata is invalid.</exception>
    Task PutAsync(string key, Stream content, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens the artifact for reading. The caller disposes the returned stream.
    /// </summary>
    /// <returns>A readable stream, or <c>null</c> when the artifact does not exist.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns size, timestamp and metadata of an artifact.
    /// </summary>
    /// <returns>The info, or <c>null</c> when the artifact does not exist.</returns>
    Task<ArtifactInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lists all artifacts whose key starts with <paramref name="prefix" /> (plain string prefix; use a
    ///     trailing <c>/</c> to list a "folder"). An empty prefix lists everything. The order is unspecified.
    ///     <see cref="ArtifactInfo.Metadata" /> is <c>null</c> in list results; call <see cref="GetInfoAsync" />
    ///     for it.
    /// </summary>
    IAsyncEnumerable<ArtifactInfo> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes an artifact.
    /// </summary>
    /// <returns><c>true</c> when the artifact existed and was deleted, <c>false</c> when it did not exist.</returns>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     App-side retention: deletes every artifact under <paramref name="prefix" /> whose
    ///     <see cref="ArtifactInfo.CreatedAt" /> is older than <paramref name="maxAge" />. Lifecycle rules of the
    ///     storage backend are the backstop, not a replacement for this call.
    /// </summary>
    /// <returns>The keys that were deleted.</returns>
    Task<IReadOnlyList<string>> DeleteOlderThanAsync(string prefix, TimeSpan maxAge,
        CancellationToken cancellationToken = default);
}
