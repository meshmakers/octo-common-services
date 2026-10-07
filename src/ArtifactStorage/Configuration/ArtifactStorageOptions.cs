namespace Meshmakers.Octo.Services.ArtifactStorage.Configuration;

/// <summary>
///     Options of the artifact store, bound from the configuration section <see cref="SectionName" />
///     (environment variables <c>OCTO_ARTIFACTSTORAGE__…</c>).
/// </summary>
/// <remarks>
///     🔴 Credentials (<c>S3:SecretAccessKey</c>, <c>AzureBlob:AccountKey</c>, <c>AzureBlob:ConnectionString</c>)
///     never go into <c>appsettings*.json</c> or Helm values: provide them as environment variables from a
///     Kubernetes Secret (or use workload identity).
/// </remarks>
public sealed class ArtifactStorageOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "ArtifactStorage";

    /// <summary>The backend. Defaults to <see cref="ArtifactStorageProvider.FileSystem" />.</summary>
    public ArtifactStorageProvider Provider { get; set; } = ArtifactStorageProvider.FileSystem;

    /// <summary>
    ///     First key segment(s) of every key built by <see cref="ArtifactKeyBuilder" />, separating several
    ///     instances that share one bucket or container. Defaults to <c>default</c>.
    /// </summary>
    public string InstancePrefix { get; set; } = "default";

    /// <summary>
    ///     When <c>true</c>, the health check writes, reads and deletes a small probe object under
    ///     <c>&lt;InstancePrefix&gt;/_health/</c>; otherwise it only lists that prefix. Defaults to <c>false</c>.
    /// </summary>
    public bool HealthCheckWriteProbe { get; set; }

    /// <summary>Settings of the <see cref="ArtifactStorageProvider.FileSystem" /> provider.</summary>
    public FileSystemArtifactStorageOptions FileSystem { get; set; } = new();

    /// <summary>Settings of the <see cref="ArtifactStorageProvider.S3" /> provider.</summary>
    public S3ArtifactStorageOptions S3 { get; set; } = new();

    /// <summary>Settings of the <see cref="ArtifactStorageProvider.AzureBlob" /> provider.</summary>
    public AzureBlobArtifactStorageOptions AzureBlob { get; set; } = new();
}

/// <summary>
///     Settings of the file system provider.
/// </summary>
public sealed class FileSystemArtifactStorageOptions
{
    /// <summary>
    ///     Root directory. Defaults to <c>&lt;temp&gt;/octo-artifacts</c> when empty, which does not survive a
    ///     container restart — set it to a mounted volume for anything but development.
    /// </summary>
    public string? RootPath { get; set; }
}

/// <summary>
///     Settings of the S3 provider.
/// </summary>
public sealed class S3ArtifactStorageOptions
{
    /// <summary>
    ///     Endpoint of an S3-compatible service, e.g. <c>https://fsn1.your-objectstorage.com</c> or
    ///     <c>https://sos-at-vie-1.exo.io</c>. Empty for AWS (then <see cref="Region" /> selects the endpoint).
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Region, e.g. <c>eu-central-1</c>, <c>fsn1</c> or <c>at-vie-1</c>. Used for request signing.</summary>
    public string? Region { get; set; }

    /// <summary>Bucket name (required).</summary>
    public string? Bucket { get; set; }

    /// <summary>Access key id. When empty, the AWS default credential chain is used.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>Secret access key. Provide via environment/secret only.</summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>Use path-style addressing (<c>endpoint/bucket/key</c>); needed for MinIO and many S3-compatible stores.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    ///     Server-side encryption requested on upload: <c>None</c> (default, the bucket default applies) or
    ///     <c>AES256</c> (SSE-S3).
    /// </summary>
    public string? ServerSideEncryption { get; set; }

    /// <summary>
    ///     Part size for multipart uploads in bytes; defaults to 16 MiB, minimum 5 MiB (S3 limit). Content that
    ///     fits into one part is uploaded with a single request. One part is buffered in memory per upload.
    /// </summary>
    public int MultipartPartSizeBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Create the bucket on first use when it does not exist (development only; defaults to <c>false</c>).</summary>
    public bool CreateBucketIfNotExists { get; set; }
}

/// <summary>
///     Settings of the Azure Blob provider. Authentication, in this order: <see cref="ConnectionString" />,
///     <see cref="AccountUrl" /> + <see cref="AccountKey" />, <see cref="AccountUrl" /> +
///     <see cref="UseManagedIdentity" /> (<c>DefaultAzureCredential</c>, e.g. AKS workload identity).
/// </summary>
public sealed class AzureBlobArtifactStorageOptions
{
    /// <summary>Storage connection string. Provide via environment/secret only.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob service URL, e.g. <c>https://myaccount.blob.core.windows.net</c>.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>Storage account key (with <see cref="AccountUrl" />). Provide via environment/secret only.</summary>
    public string? AccountKey { get; set; }

    /// <summary>Authenticate with <c>DefaultAzureCredential</c> (workload or managed identity) against <see cref="AccountUrl" />.</summary>
    public bool UseManagedIdentity { get; set; }

    /// <summary>Optional client id of a user-assigned managed identity (with <see cref="UseManagedIdentity" />).</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Container name (required).</summary>
    public string? Container { get; set; }

    /// <summary>Create the container on first use when it does not exist (development only; defaults to <c>false</c>).</summary>
    public bool CreateContainerIfNotExists { get; set; }
}
