namespace Meshmakers.Octo.Services.ArtifactStorage;

/// <summary>
///     Describes a stored artifact.
/// </summary>
/// <param name="Key">The artifact key.</param>
/// <param name="Size">Content length in bytes.</param>
/// <param name="CreatedAt">
///     UTC time the current content was written (file last-write time, S3 <c>LastModified</c>, blob
///     <c>Last-Modified</c>). Overwriting a key resets it. Retention compares against this value;
///     <see cref="DateTimeOffset.MinValue" /> means the provider reported no time, and such an artifact never expires
///     through <see cref="IArtifactStore.DeleteOlderThanAsync" />.
/// </param>
/// <param name="Metadata">Content type and properties; <c>null</c> in <see cref="IArtifactStore.ListAsync" /> results.</param>
public sealed record ArtifactInfo(string Key, long Size, DateTimeOffset CreatedAt, ArtifactMetadata? Metadata);
