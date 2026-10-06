using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;

/// <summary>
///     Stores artifacts as block blobs in an Azure Blob Storage container.
/// </summary>
/// <remarks>
///     Uploads are streamed in blocks by the Azure SDK. Authentication: connection string, account key, or
///     <c>DefaultAzureCredential</c> (workload or managed identity) — see <see cref="AzureBlobArtifactStorageOptions" />.
///     The container must be private; blob soft delete, versioning and immutability policies must be off for it,
///     otherwise deleted artifacts outlive their retention.
/// </remarks>
public sealed class AzureBlobArtifactStore : ArtifactStoreBase
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _containerLock = new(1, 1);
    private readonly bool _createContainerIfNotExists;
    private readonly ILogger<AzureBlobArtifactStore> _logger;
    private volatile bool _containerEnsured;

    /// <summary>
    ///     Creates the store with a container client built from <paramref name="options" />.
    /// </summary>
    public AzureBlobArtifactStore(AzureBlobArtifactStorageOptions options, TimeProvider? timeProvider = null,
        ILogger<AzureBlobArtifactStore>? logger = null)
        : this(CreateContainerClient(options), options.CreateContainerIfNotExists, timeProvider, logger)
    {
    }

    /// <summary>
    ///     Creates the store with an existing container client.
    /// </summary>
    public AzureBlobArtifactStore(BlobContainerClient container, bool createContainerIfNotExists = false,
        TimeProvider? timeProvider = null, ILogger<AzureBlobArtifactStore>? logger = null)
        : base(timeProvider)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
        _createContainerIfNotExists = createContainerIfNotExists;
        _logger = logger ?? NullLogger<AzureBlobArtifactStore>.Instance;
    }

    /// <inheritdoc />
    public override string ProviderName => "AzureBlob";

    /// <summary>
    ///     Builds a <see cref="BlobContainerClient" /> from <paramref name="options" />.
    /// </summary>
    /// <exception cref="ArgumentException">No usable authentication is configured.</exception>
    public static BlobContainerClient CreateContainerClient(AzureBlobArtifactStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Container, nameof(options.Container));

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, options.Container);
        }

        if (string.IsNullOrWhiteSpace(options.AccountUrl) ||
            !Uri.TryCreate(options.AccountUrl, UriKind.Absolute, out var accountUri))
        {
            throw new ArgumentException("AzureBlob needs a ConnectionString or an absolute AccountUrl.",
                nameof(options));
        }

        var containerUri = new BlobUriBuilder(accountUri) { BlobContainerName = options.Container }.ToUri();
        if (!string.IsNullOrWhiteSpace(options.AccountKey))
        {
            var accountName = new BlobUriBuilder(accountUri).AccountName;
            if (string.IsNullOrEmpty(accountName))
            {
                throw new ArgumentException("The account name cannot be derived from AzureBlob:AccountUrl.",
                    nameof(options));
            }

            return new BlobContainerClient(containerUri,
                new StorageSharedKeyCredential(accountName, options.AccountKey));
        }

        if (options.UseManagedIdentity)
        {
            var credentialOptions = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
            {
                credentialOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
                credentialOptions.WorkloadIdentityClientId = options.ManagedIdentityClientId;
            }

            return new BlobContainerClient(containerUri, new DefaultAzureCredential(credentialOptions));
        }

        throw new ArgumentException("AzureBlob:AccountUrl needs AccountKey or UseManagedIdentity=true.",
            nameof(options));
    }

    /// <inheritdoc />
    public override async Task PutAsync(string key, Stream content, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ValidatePut(key, content, metadata);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = metadata.EffectiveContentType },
            Metadata = new Dictionary<string, string>(metadata.Properties)
        };
        await _container.GetBlobClient(key).UploadAsync(content, options, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Stored artifact {ArtifactKey} in container {Container}", key, _container.Name);
    }

    /// <inheritdoc />
    public override async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        try
        {
            var response = await _container.GetBlobClient(key)
                .DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ResponseOwningStream(response.Value.Content, response.Value);
        }
        catch (RequestFailedException e) when (IsBlobNotFound(e))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override async Task<ArtifactInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        try
        {
            var properties = (await _container.GetBlobClient(key)
                .GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            var contentType = string.IsNullOrEmpty(properties.ContentType)
                ? ArtifactMetadata.DefaultContentType
                : properties.ContentType;
            var metadata = properties.Metadata.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value,
                StringComparer.Ordinal);
            return new ArtifactInfo(key, properties.ContentLength, properties.LastModified.ToUniversalTime(),
                new ArtifactMetadata(contentType, metadata));
        }
        catch (RequestFailedException e) when (IsBlobNotFound(e))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactInfo> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArtifactKey.ValidatePrefix(prefix);
        await foreach (var item in _container
                           .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix.Length == 0 ? null : prefix,
                               cancellationToken)
                           .ConfigureAwait(false))
        {
            // Blobs written by other tools may not follow the key rules; they are not artifacts.
            if (!ArtifactKey.IsValid(item.Name))
            {
                continue;
            }

            yield return new ArtifactInfo(item.Name, item.Properties.ContentLength ?? 0,
                item.Properties.LastModified?.ToUniversalTime() ?? DateTimeOffset.MinValue, null);
        }
    }

    /// <inheritdoc />
    public override async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        var response = await _container.GetBlobClient(key)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (response.Value)
        {
            _logger.LogDebug("Deleted artifact {ArtifactKey} from container {Container}", key, _container.Name);
        }

        return response.Value;
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (!_createContainerIfNotExists || _containerEnsured)
        {
            return;
        }

        await _containerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_containerEnsured)
            {
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _containerEnsured = true;
            }
        }
        finally
        {
            _containerLock.Release();
        }
    }

    private static bool IsBlobNotFound(RequestFailedException e)
    {
        // A missing container is a configuration error and must surface, not read as "artifact not found".
        return e.Status == 404 && e.ErrorCode != BlobErrorCode.ContainerNotFound;
    }
}
