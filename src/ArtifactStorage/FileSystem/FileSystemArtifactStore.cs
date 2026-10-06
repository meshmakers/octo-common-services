using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Services.ArtifactStorage.FileSystem;

/// <summary>
///     Stores artifacts as files below a root directory. Key segments map to directories.
/// </summary>
/// <remarks>
///     <para>
///         On Unix, directories are created with mode <c>0700</c> and files with <c>0600</c>. Uploads go to a
///         temporary file in the target directory and are moved into place, so readers never see a partial
///         artifact. Metadata lives in a hidden sidecar file (<c>.&lt;name&gt;.meta.json</c>); names starting
///         with <c>.</c> are never valid key segments, so internal files never collide with artifacts and are
///         skipped by <see cref="ListAsync" />.
///     </para>
///     <para>
///         The file system may be case-insensitive (macOS, Windows), and file permissions are ignored on SMB
///         mounts.
///     </para>
/// </remarks>
public sealed class FileSystemArtifactStore : ArtifactStoreBase
{
    private const UnixFileMode UnixDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode UnixArtifactFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string MetadataSuffix = ".meta.json";
    private const string UploadSuffix = ".upload";
    private const int BufferSize = 81920;

    private static readonly ArtifactMetadata DefaultMetadata = new(ArtifactMetadata.DefaultContentType);

    private readonly ILogger<FileSystemArtifactStore> _logger;
    private readonly string _rootPath;

    /// <summary>
    ///     Creates the store.
    /// </summary>
    /// <param name="rootPath">Root directory; created on first write when missing.</param>
    /// <param name="timeProvider">Clock for retention.</param>
    /// <param name="logger">Logger.</param>
    public FileSystemArtifactStore(string rootPath, TimeProvider? timeProvider = null,
        ILogger<FileSystemArtifactStore>? logger = null)
        : base(timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _logger = logger ?? NullLogger<FileSystemArtifactStore>.Instance;
    }

    /// <summary>The absolute root directory.</summary>
    public string RootPath => _rootPath;

    /// <inheritdoc />
    public override string ProviderName => "FileSystem";

    /// <inheritdoc />
    public override async Task PutAsync(string key, Stream content, ArtifactMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ValidatePut(key, content, metadata);
        var path = GetPath(key);
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        CreateDirectory(directory);

        var tempContent = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}{UploadSuffix}");
        var tempMetadata = Path.Combine(directory, $".{name}.{Guid.NewGuid():N}{UploadSuffix}");
        try
        {
            await using (var target = new FileStream(tempContent, CreateWriteOptions()))
            {
                await content.CopyToAsync(target, BufferSize, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var metadataPath = GetMetadataPath(path);
            if (metadata.EffectiveContentType != ArtifactMetadata.DefaultContentType || metadata.Properties.Count > 0)
            {
                await using (var target = new FileStream(tempMetadata, CreateWriteOptions()))
                {
                    await JsonSerializer.SerializeAsync(target,
                        new MetadataDocument(metadata.EffectiveContentType,
                            new Dictionary<string, string>(metadata.Properties)),
                        FileSystemJsonContext.Default.MetadataDocument, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempMetadata, metadataPath, true);
            }
            else
            {
                File.Delete(metadataPath);
            }

            File.Move(tempContent, path, true);
            _logger.LogDebug("Stored artifact {ArtifactKey} in the file system store", key);
        }
        finally
        {
            TryDelete(tempContent);
            TryDelete(tempMetadata);
        }
    }

    /// <inheritdoc />
    public override Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        var path = GetPath(key);
        try
        {
            Stream stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = BufferSize
            });
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            // The key names a "folder" (a prefix of other keys), not an artifact.
            return Task.FromResult<Stream?>(null);
        }
    }

    /// <inheritdoc />
    public override async Task<ArtifactInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        var path = GetPath(key);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        var metadata = await ReadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        return new ArtifactInfo(key, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            metadata);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ArtifactInfo> ListAsync(string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArtifactKey.ValidatePrefix(prefix);
        var lastSeparator = prefix.LastIndexOf(ArtifactKey.Separator);
        var baseDirectory = lastSeparator < 0
            ? _rootPath
            : Path.Combine(_rootPath, prefix[..lastSeparator].Replace(ArtifactKey.Separator, Path.DirectorySeparatorChar));
        if (!Directory.Exists(baseDirectory))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        var results = new List<ArtifactInfo>();
        foreach (var file in new DirectoryInfo(baseDirectory).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_rootPath, file.FullName)
                .Replace(Path.DirectorySeparatorChar, ArtifactKey.Separator);

            // Hidden files (uploads in progress, metadata sidecars) and anything that is not a valid key
            // (e.g. files placed by hand inside a hidden directory) are not artifacts.
            if (!ArtifactKey.IsValid(relative) || !relative.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new ArtifactInfo(relative, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), null));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var info in results.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            yield return info;
        }
    }

    /// <inheritdoc />
    public override Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArtifactKey.Validate(key);
        var path = GetPath(key);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        TryDelete(GetMetadataPath(path));
        PruneEmptyDirectories(Path.GetDirectoryName(path)!);
        _logger.LogDebug("Deleted artifact {ArtifactKey} from the file system store", key);
        return Task.FromResult(true);
    }

    private string GetPath(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_rootPath,
            key.Replace(ArtifactKey.Separator, Path.DirectorySeparatorChar)));

        // Defence in depth: key validation already rules out '..' and rooted segments.
        if (!path.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Artifact key '{key}' resolves outside the store root.", nameof(key));
        }

        return path;
    }

    private static string GetMetadataPath(string path)
    {
        return Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}{MetadataSuffix}");
    }

    private static async Task<ArtifactMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var metadataPath = GetMetadataPath(path);
        try
        {
            await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = await JsonSerializer
                .DeserializeAsync(stream, FileSystemJsonContext.Default.MetadataDocument, cancellationToken)
                .ConfigureAwait(false);
            return document == null
                ? DefaultMetadata
                : new ArtifactMetadata(document.ContentType ?? ArtifactMetadata.DefaultContentType,
                    document.Properties);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return DefaultMetadata;
        }
    }

    private void CreateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            // Directory.CreateDirectory(path, mode) applies the mode to the leaf only; create every missing
            // level (root included) explicitly so no intermediate directory ends up world-readable.
            var missing = new Stack<string>();
            for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current)!)
            {
                missing.Push(current);
                if (string.Equals(current, _rootPath, StringComparison.Ordinal))
                {
                    break;
                }
            }

            while (missing.Count > 0)
            {
                Directory.CreateDirectory(missing.Pop(), UnixDirectoryMode);
            }
        }
    }

    private static FileStreamOptions CreateWriteOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = BufferSize
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixArtifactFileMode;
        }

        return options;
    }

    private void PruneEmptyDirectories(string directory)
    {
        try
        {
            while (directory.Length > _rootPath.Length &&
                   directory.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory)!;
            }
        }
        catch (IOException)
        {
            // A concurrent write created a new entry; the directory stays.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record MetadataDocument(string? ContentType, Dictionary<string, string>? Properties);
}
