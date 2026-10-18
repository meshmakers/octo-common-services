using Azure.Storage.Sas;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;

namespace Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;

/// <summary>
///     Issues the container-level shared access signature (SAS) a native archive dump or restore run hands to CrateDB
///     in the <c>sas_token</c> option of <c>COPY</c> (AB#5967, AB#6969).
/// </summary>
/// <remarks>
///     <para>
///         <b>Scope is the container.</b> Azure cannot scope a SAS to a blob prefix without a hierarchical namespace
///         (and the artifact containers have none), so the token covers the whole artifact container for its lifetime
///         — the other categories and instances that share the container included. That is why the lifetime is short
///         (default 2 hours, <c>AzureBlob:StagingSasLifetime</c>), the permissions are the smallest set the direction
///         needs, and the token is issued per run and never logged.
///     </para>
///     <para>
///         Dump: create, write, list. Restore: read, list. No delete, no add, no tags. HTTPS only, except for an
///         emulator endpoint that is plain HTTP. The start time is a few minutes in the past so that a clock difference
///         between this service and the storage service does not refuse a fresh token.
///     </para>
/// </remarks>
internal static class AzureStagingSas
{
    /// <summary>How far the token's start lies before now (clock skew between service and storage).</summary>
    public static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(5);

    /// <summary>Returns the query string (without <c>?</c>) of the SAS: <c>sv=…&amp;sr=c&amp;sig=…</c>.</summary>
    public static string Create(AzureStagingAccount account, ArchiveCopyStagingAccess access, DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = account.Container,
            Resource = "c",
            StartsOn = now - StartSkew,
            ExpiresOn = now + lifetime,
            Protocol = account.SasProtocol
        };
        builder.SetPermissions(access switch
        {
            ArchiveCopyStagingAccess.Dump => BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write |
                                             BlobContainerSasPermissions.List,
            ArchiveCopyStagingAccess.Restore => BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unknown staging access.")
        });

        return builder.ToSasQueryParameters(account.CreateCredential()).ToString();
    }
}
