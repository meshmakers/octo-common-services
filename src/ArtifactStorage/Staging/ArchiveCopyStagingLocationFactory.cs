using System.Text;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.ArtifactStorage.Staging;

/// <summary>
///     Creates <see cref="ArchiveCopyStagingLocation" />s from <see cref="ArtifactStorageOptions" />
///     (see <see cref="IArchiveCopyStagingLocationFactory" />).
/// </summary>
/// <remarks>
///     <para>
///         <b>S3</b>: <c>s3://&lt;key&gt;:&lt;secret&gt;@&lt;host&gt;:&lt;port&gt;/&lt;bucket&gt;/&lt;prefix&gt;</c> with the option
///         <c>protocol=http|https</c>. Host, port and protocol come from <see cref="S3ArtifactStorageOptions.CrateDbServiceUrl" />
///         when set (the endpoint CrateDB reaches), otherwise from <see cref="S3ArtifactStorageOptions.ServiceUrl" />. Key and
///         secret are percent-encoded, so a secret with <c>/</c>, <c>+</c>, <c>@</c> or <c>:</c> survives the URI. Without
///         an endpoint (AWS) the host part is omitted and CrateDB uses its default endpoint; without static keys the user
///         info is omitted. A key or secret that contains <c>:</c> is not supported (CrateDB splits the decoded user info at
///         the first colon, also when it was percent-encoded); AWS-style keys do not contain one.
///     </para>
///     <para>
///         <b>Azure Blob</b>: <c>az://&lt;account&gt;.&lt;suffix&gt;/&lt;container&gt;/&lt;prefix&gt;</c> with the option
///         <c>sas_token</c>, a container SAS per call (<see cref="AzureStagingSas" />). The credential is never part of the
///         URI.
///     </para>
///     <para>
///         The factory logs the redacted location only and puts no credential into an exception.
///     </para>
/// </remarks>
public sealed class ArchiveCopyStagingLocationFactory : IArchiveCopyStagingLocationFactory
{
    private readonly AzureStagingAccount? _azureAccount;
    private readonly ArtifactKeyBuilder _keyBuilder;
    private readonly ILogger<ArchiveCopyStagingLocationFactory> _logger;
    private readonly ArtifactStorageOptions _options;
    private readonly Uri? _s3CrateEndpoint;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the factory from the configured options.</summary>
    public ArchiveCopyStagingLocationFactory(IOptions<ArtifactStorageOptions> options, ArtifactKeyBuilder keyBuilder,
        TimeProvider? timeProvider = null, ILogger<ArchiveCopyStagingLocationFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyBuilder);
        _options = options.Value;
        _keyBuilder = keyBuilder;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ArchiveCopyStagingLocationFactory>.Instance;

        switch (_options.Provider)
        {
            case ArtifactStorageProvider.S3:
                UnsupportedReason = ResolveS3(_options.S3, out _s3CrateEndpoint);
                break;
            case ArtifactStorageProvider.AzureBlob:
                AzureStagingAccount.TryResolve(_options.AzureBlob, out _azureAccount, out var reason);
                UnsupportedReason = reason;
                break;
            default:
                UnsupportedReason =
                    $"The {_options.Provider} artifact store cannot be reached by the CrateDB nodes; the archive copy " +
                    $"needs {nameof(ArtifactStorageProvider.S3)} or {nameof(ArtifactStorageProvider.AzureBlob)} " +
                    "(the dump and restore run as format 1, rows through the service).";
                break;
        }
    }

    /// <inheritdoc />
    public bool IsSupported => UnsupportedReason == null;

    /// <inheritdoc />
    public string? UnsupportedReason { get; }

    /// <inheritdoc />
    public ArchiveCopyStagingLocation Create(string tenantId, string runId, ArchiveCopyStagingAccess access,
        string? subPath = null)
    {
        if (UnsupportedReason != null)
        {
            throw new ArchiveCopyStagingNotSupportedException(UnsupportedReason);
        }

        var prefix = _keyBuilder.RunPrefix(ArtifactCategories.CopyStaging, tenantId, runId).TrimEnd('/');
        var location = _options.Provider == ArtifactStorageProvider.S3
            ? CreateS3(prefix)
            : CreateAzure(prefix, access);

        if (subPath != null)
        {
            location = location.WithSubPath(subPath);
        }

        _logger.LogDebug("Created {Access} staging location {StagingLocation}", access, location);
        return location;
    }

    private static string? ResolveS3(S3ArtifactStorageOptions s3, out Uri? crateEndpoint)
    {
        crateEndpoint = null;
        if (string.IsNullOrWhiteSpace(s3.Bucket))
        {
            return $"{ArtifactStorageOptions.SectionName}:S3:Bucket is not set.";
        }

        if ((s3.AccessKeyId?.Contains(':') ?? false) || (s3.SecretAccessKey?.Contains(':') ?? false))
        {
            // CrateDB splits the decoded user info of the URI at the first ':' — even a percent-encoded one — so
            // the request would be signed with the wrong key (verified against CrateDB 5.9.10 and MinIO).
            return $"{ArtifactStorageOptions.SectionName}:S3:AccessKeyId or {ArtifactStorageOptions.SectionName}:S3:SecretAccessKey " +
                   "contains ':', which CrateDB cannot take in a COPY location URI.";
        }

        var (setting, value) = string.IsNullOrWhiteSpace(s3.CrateDbServiceUrl)
            ? ("ServiceUrl", s3.ServiceUrl)
            : ("CrateDbServiceUrl", s3.CrateDbServiceUrl);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null; // AWS: CrateDB uses its default endpoint
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"{ArtifactStorageOptions.SectionName}:S3:{setting} is not an absolute http or https URL, " +
                   "so CrateDB cannot be given an endpoint (set CrateDbServiceUrl).";
        }

        crateEndpoint = uri;
        return null;
    }

    private ArchiveCopyStagingLocation CreateS3(string prefix)
    {
        var s3 = _options.S3;
        var uri = new StringBuilder("s3://");
        if (!string.IsNullOrEmpty(s3.AccessKeyId) && !string.IsNullOrEmpty(s3.SecretAccessKey))
        {
            uri.Append(Uri.EscapeDataString(s3.AccessKeyId)).Append(':').Append(Uri.EscapeDataString(s3.SecretAccessKey))
                .Append('@');
        }

        var options = new Dictionary<string, string>();
        if (_s3CrateEndpoint != null)
        {
            // The port is always explicit: "[<host>:<port>/]<bucket>" is the form CrateDB documents.
            uri.Append(_s3CrateEndpoint.Host).Append(':').Append(_s3CrateEndpoint.Port).Append('/');
            options["protocol"] = _s3CrateEndpoint.Scheme;
        }

        uri.Append(s3.Bucket).Append('/').Append(prefix);
        return new ArchiveCopyStagingLocation(uri.ToString(), options);
    }

    private ArchiveCopyStagingLocation CreateAzure(string prefix, ArchiveCopyStagingAccess access)
    {
        var account = _azureAccount!;
        var endpoint = account.BlobEndpoint;
        var uri = $"az://{endpoint.Authority}{endpoint.AbsolutePath.TrimEnd('/')}/{account.Container}/{prefix}";

        var options = new Dictionary<string, string>
        {
            ["sas_token"] = AzureStagingSas.Create(account, access, _timeProvider.GetUtcNow(),
                _options.AzureBlob.StagingSasLifetime)
        };
        if (endpoint.Scheme != Uri.UriSchemeHttps)
        {
            options["protocol"] = endpoint.Scheme;
        }

        return new ArchiveCopyStagingLocation(uri, options);
    }
}
