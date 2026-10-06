using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Services.ArtifactStorage.S3;

/// <summary>
///     Stores artifacts in an S3-compatible bucket (AWS S3, Hetzner Object Storage, Exoscale SOS, MinIO).
/// </summary>
/// <remarks>
///     <para>
///         Uploads are streamed: content that fits into one part (<see cref="S3ArtifactStorageOptions.MultipartPartSizeBytes" />)
///         is sent with a single <c>PutObject</c>, larger content as a multipart upload with one part buffered at a
///         time. A failed multipart upload is aborted.
///     </para>
///     <para>
///         The client uses the "when required" checksum mode, because several S3-compatible stores reject the
///         default checksum headers of AWS SDK v4.
///     </para>
///     <para>
///         <see cref="GetInfoAsync" /> uses a <c>HEAD</c> request; S3 answers a missing bucket and a missing key
///         alike there, so a missing bucket reports "not found" — the health check (a list request) detects it.
///     </para>
/// </remarks>
public sealed class S3ArtifactStore : ArtifactStoreBase, IDisposable
{
    private const string MetadataHeaderPrefix = "x-amz-meta-";

    private readonly string _bucket;
    private readonly IAmazonS3 _client;
    private readonly ILogger<S3ArtifactStore> _logger;
    private readonly S3ArtifactStorageOptions _options;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private volatile bool _bucketEnsured;

    /// <summary>
    ///     Creates the store with a client built from <paramref name="options" />.
    /// </summary>
    public S3ArtifactStore(S3ArtifactStorageOptions options, TimeProvider? timeProvider = null,
        ILogger<S3ArtifactStore>? logger = null)
        : this(CreateClient(options), options, timeProvider, logger, true)
    {
    }

    /// <summary>
    ///     Creates the store with an existing client (not disposed by the store).
    /// </summary>
    public S3ArtifactStore(IAmazonS3 client, S3ArtifactStorageOptions options, TimeProvider? timeProvider = null,
        ILogger<S3ArtifactStore>? logger = null)
        : this(client, options, timeProvider, logger, false)
    {
    }

    private S3ArtifactStore(IAmazonS3 client, S3ArtifactStorageOptions options, TimeProvider? timeProvider,
        ILogger<S3ArtifactStore>? logger, bool ownsClient)
        : base(timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket, nameof(options.Bucket));
        if (options.MultipartPartSizeBytes < ArtifactStorageOptionsValidator.MinS3PartSize)
        {
            throw new ArgumentException(
                $"MultipartPartSizeBytes must be at least {ArtifactStorageOptionsValidator.MinS3PartSize}.",
                nameof(options));
        }

        if (!IsSupportedServerSideEncryption(options.ServerSideEncryption))
        {
            throw new ArgumentException("ServerSideEncryption must be 'None' or 'AES256'.", nameof(options));
        }

        _client = client;
        _options = options;
        _bucket = options.Bucket;
        _ownsClient = ownsClient;
        _logger = logger ?? NullLogger<S3ArtifactStore>.Instance;
    }

    /// <inheritdoc />
    public override string ProviderName => "S3";

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        _bucketLock.Dispose();
    }

    /// <summary>Returns <c>true</c> for an empty value, <c>None</c> or <c>AES256</c> (case-insensitive).</summary>
    public static bool IsSupportedServerSideEncryption(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "AES256", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds an <see cref="AmazonS3Client" /> from <paramref name="options" />: static keys when configured,
    ///     otherwise the AWS default credential chain.
    /// </summary>
    public static AmazonS3Client CreateClient(S3ArtifactStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            if (!string.IsNullOrWhiteSpace(options.Region))
            {
                config.AuthenticationRegion = options.Region;
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (!string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey))
        {
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
        }

        return new AmazonS3Client(config);
    }

    /// <inheritdoc />
    public override async Task PutAsync(string key, Stream content, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ValidatePut(key, content, metadata);
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        var partSize = _options.MultipartPartSizeBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(partSize);
        try
        {
            var read = await ReadFullAsync(content, buffer, partSize, cancellationToken).ConfigureAwait(false);
            if (read < partSize)
            {
                using var body = new MemoryStream(buffer, 0, read, false);
                var request = new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = body,
                    AutoCloseStream = false,
                    ContentType = metadata.EffectiveContentType
                };
                ApplyEncryption(request);
                AddMetadata(request.Metadata, metadata);
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await MultipartUploadAsync(key, content, metadata, buffer, read, cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogDebug("Stored artifact {ArtifactKey} in bucket {Bucket}", key, _bucket);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public override async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key },
                cancellationToken).ConfigureAwait(false);
            return new ResponseOwningStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception e) when (IsKeyNotFound(e))
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
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in response.Metadata.Keys)
            {
                var propertyName = name.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? name[MetadataHeaderPrefix.Length..]
                    : name;
                properties[propertyName.ToLowerInvariant()] = response.Metadata[name];
            }

            var contentType = string.IsNullOrEmpty(response.Headers.ContentType)
                ? ArtifactMetadata.DefaultContentType
                : response.Headers.ContentType;
            return new ArtifactInfo(key, response.Headers.ContentLength, ToUtc(response.LastModified),
                new ArtifactMetadata(contentType, properties));
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactInfo> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArtifactKey.ValidatePrefix(prefix);
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix.Length == 0 ? null : prefix };
        while (true)
        {
            var response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                // Objects written by other tools may not follow the key rules; they are not artifacts.
                if (ArtifactKey.IsValid(item.Key))
                {
                    yield return new ArtifactInfo(item.Key, item.Size ?? 0, ToUtc(item.LastModified), null);
                }
            }

            if (response.IsTruncated != true || string.IsNullOrEmpty(response.NextContinuationToken))
            {
                yield break;
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
    }

    /// <inheritdoc />
    public override async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);

        // S3 answers DELETE with 204 whether the key existed or not; HEAD first to report it.
        if (await GetInfoAsync(key, cancellationToken).ConfigureAwait(false) == null)
        {
            return false;
        }

        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key },
            cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Deleted artifact {ArtifactKey} from bucket {Bucket}", key, _bucket);
        return true;
    }

    private async Task MultipartUploadAsync(string key, Stream content, ArtifactMetadata metadata, byte[] buffer,
        int firstPartLength, CancellationToken cancellationToken)
    {
        var initiate = new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentType = metadata.EffectiveContentType
        };
        if (IsAes256())
        {
            initiate.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        AddMetadata(initiate.Metadata, metadata);
        var upload = await _client.InitiateMultipartUploadAsync(initiate, cancellationToken).ConfigureAwait(false);
        try
        {
            var partETags = new List<PartETag>();
            var length = firstPartLength;
            var partNumber = 1;
            while (length > 0)
            {
                using var body = new MemoryStream(buffer, 0, length, false);
                var part = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = upload.UploadId,
                    PartNumber = partNumber,
                    PartSize = length,
                    InputStream = body
                }, cancellationToken).ConfigureAwait(false);
                partETags.Add(new PartETag(partNumber, part.ETag));
                partNumber++;
                length = await ReadFullAsync(content, buffer, _options.MultipartPartSizeBytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = upload.UploadId,
                PartETags = partETags
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = upload.UploadId
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abortException)
            {
                _logger.LogWarning(abortException,
                    "Aborting the multipart upload of artifact {ArtifactKey} failed; the bucket's lifecycle rule for incomplete uploads has to clean it up",
                    key);
            }

            throw;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (!_options.CreateBucketIfNotExists || _bucketEnsured)
        {
            return;
        }

        await _bucketLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bucketEnsured)
            {
                return;
            }

            try
            {
                await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Created artifact bucket {Bucket}", _bucket);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
            }

            _bucketEnsured = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    private bool IsAes256()
    {
        return string.Equals(_options.ServerSideEncryption, "AES256", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyEncryption(PutObjectRequest request)
    {
        if (IsAes256())
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }
    }

    private static void AddMetadata(MetadataCollection target, ArtifactMetadata metadata)
    {
        foreach (var (name, value) in metadata.Properties)
        {
            target.Add(name, value);
        }
    }

    private static bool IsKeyNotFound(AmazonS3Exception e)
    {
        return e.ErrorCode == "NoSuchKey" ||
               (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode != "NoSuchBucket");
    }

    private static DateTimeOffset ToUtc(DateTime? value)
    {
        if (value == null)
        {
            return DateTimeOffset.MinValue;
        }

        var dateTime = value.Value;
        return dateTime.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dateTime, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero)
        };
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int count,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
