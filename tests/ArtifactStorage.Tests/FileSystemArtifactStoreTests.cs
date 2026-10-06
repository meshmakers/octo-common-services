using System.Runtime.Versioning;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;

namespace ArtifactStorage.Tests;

/// <summary>Runs the contract suite against the file system provider, plus file-system specifics.</summary>
public sealed class FileSystemArtifactStoreTests : ArtifactStoreContractTests, IDisposable
{
    private readonly string _rootPath =
        Path.Combine(Path.GetTempPath(), "octo-artifact-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    protected override Task<IArtifactStore> CreateStoreAsync(TimeProvider? timeProvider = null)
    {
        return Task.FromResult<IArtifactStore>(new FileSystemArtifactStore(_rootPath, timeProvider));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Put_OnUnix_CreatesPrivateDirectoriesAndFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only.");
        var store = new FileSystemArtifactStore(_rootPath);

        await store.PutAsync("inst/presweep/t1/dump.octoenc", new MemoryStream([1, 2, 3]),
            new ArtifactMetadata("application/gzip"), Ct);

        const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(dirMode, File.GetUnixFileMode(_rootPath));
        Assert.Equal(dirMode, File.GetUnixFileMode(Path.Combine(_rootPath, "inst")));
        Assert.Equal(dirMode, File.GetUnixFileMode(Path.Combine(_rootPath, "inst", "presweep", "t1")));
        Assert.Equal(fileMode, File.GetUnixFileMode(Path.Combine(_rootPath, "inst", "presweep", "t1", "dump.octoenc")));
        Assert.Equal(fileMode,
            File.GetUnixFileMode(Path.Combine(_rootPath, "inst", "presweep", "t1", ".dump.octoenc.meta.json")));
    }

    [Fact]
    public async Task InternalFiles_AreNotListedAndNoUploadLeftovers()
    {
        var store = new FileSystemArtifactStore(_rootPath);
        await store.PutAsync("a/file.bin", new MemoryStream([1]),
            new ArtifactMetadata("text/plain", new Dictionary<string, string> { ["x"] = "y" }), Ct);
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "a", ".foreign.upload"), "partial", Ct);

        var keys = await ListKeysAsync(store, "");

        Assert.Equal(["a/file.bin"], keys);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_rootPath, "a")),
            f => f.EndsWith(".upload", StringComparison.Ordinal) && !f.EndsWith(".foreign.upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedUpload_LeavesNoArtifactAndKeepsThePreviousVersion()
    {
        var store = new FileSystemArtifactStore(_rootPath);
        await store.PutAsync("a/file.bin", new MemoryStream([1, 2, 3]), ArtifactMetadata.None, Ct);

        await Assert.ThrowsAsync<IOException>(() =>
            store.PutAsync("a/file.bin", new FailingStream(), ArtifactMetadata.None, Ct));

        Assert.Equal(3, (await store.GetInfoAsync("a/file.bin", Ct))?.Size);
        Assert.Equal(["file.bin"], Directory.GetFiles(Path.Combine(_rootPath, "a")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Delete_PrunesEmptyDirectoriesButKeepsRoot()
    {
        var store = new FileSystemArtifactStore(_rootPath);
        await store.PutAsync("inst/presweep/t1/dump.bin", new MemoryStream([1]), ArtifactMetadata.None, Ct);
        await store.PutAsync("inst/other.bin", new MemoryStream([1]), ArtifactMetadata.None, Ct);

        Assert.True(await store.DeleteAsync("inst/presweep/t1/dump.bin", Ct));

        Assert.False(Directory.Exists(Path.Combine(_rootPath, "inst", "presweep")));
        Assert.True(Directory.Exists(Path.Combine(_rootPath, "inst")));
        Assert.True(Directory.Exists(_rootPath));
    }

    [Fact]
    public async Task FolderKey_IsNotAnArtifact()
    {
        var store = new FileSystemArtifactStore(_rootPath);
        await store.PutAsync("folder/file.bin", new MemoryStream([1]), ArtifactMetadata.None, Ct);

        Assert.Null(await store.OpenReadAsync("folder", Ct));
        Assert.Null(await store.GetInfoAsync("folder", Ct));
        Assert.False(await store.DeleteAsync("folder", Ct));
    }

    [Fact]
    public async Task List_OfMissingRoot_IsEmpty()
    {
        var store = new FileSystemArtifactStore(Path.Combine(_rootPath, "does-not-exist"));

        Assert.Empty(await ListKeysAsync(store, ""));
        Assert.Empty(await ListKeysAsync(store, "a/b/"));
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0)
            {
                throw new IOException("Simulated network failure.");
            }

            buffer[offset] = 42;
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0)
            {
                throw new IOException("Simulated network failure.");
            }

            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
