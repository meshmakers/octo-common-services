namespace Meshmakers.Octo.Services.ArtifactStorage.Configuration;

/// <summary>
///     The storage backend behind <see cref="IArtifactStore" />.
/// </summary>
public enum ArtifactStorageProvider
{
    /// <summary>A local directory (development, or a mounted volume as fallback).</summary>
    FileSystem = 0,

    /// <summary>An S3-compatible bucket (AWS, Hetzner Object Storage, Exoscale SOS, MinIO).</summary>
    S3 = 1,

    /// <summary>An Azure Blob Storage container.</summary>
    AzureBlob = 2
}
