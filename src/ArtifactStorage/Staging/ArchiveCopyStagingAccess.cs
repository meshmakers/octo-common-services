namespace Meshmakers.Octo.Services.ArtifactStorage.Staging;

/// <summary>
///     What the CrateDB nodes do with a staging location. It decides the permissions of a credential issued for it.
/// </summary>
public enum ArchiveCopyStagingAccess
{
    /// <summary>A dump: <c>COPY … TO</c> writes the files (write, create, list).</summary>
    Dump = 0,

    /// <summary>A restore: <c>COPY … FROM</c> reads the files (read, list).</summary>
    Restore = 1
}
