using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;

namespace Meshmakers.Octo.Services.ArtifactStorage.AzureBlob;

/// <summary>
///     What a container-level shared access signature (SAS) for the CrateDB <c>COPY</c> statements needs from the
///     Azure Blob settings: account name, account key, blob service endpoint and container (AB#5967, AB#6969).
/// </summary>
/// <remarks>
///     A container SAS is signed with the <b>account key</b>. A store configured with a managed identity or with a
///     connection string without <c>AccountKey</c> has no key, so it cannot issue one (a user delegation SAS would be
///     needed; that is phase 2) — <see cref="TryResolve" /> then reports the reason. The reason names settings, never
///     values. The key is reachable only through <see cref="CreateCredential" />; <see cref="ToString" /> shows
///     account and container only.
/// </remarks>
internal sealed class AzureStagingAccount
{
    private readonly string _accountKey;

    private AzureStagingAccount(string accountName, string accountKey, Uri blobEndpoint, string container)
    {
        AccountName = accountName;
        _accountKey = accountKey;
        BlobEndpoint = blobEndpoint;
        Container = container;
    }

    /// <summary>The storage account name.</summary>
    public string AccountName { get; }

    /// <summary>
    ///     The blob service endpoint, e.g. <c>https://account.blob.core.windows.net</c>; the emulator form carries the
    ///     account in the path (<c>http://127.0.0.1:10000/devstoreaccount1</c>).
    /// </summary>
    public Uri BlobEndpoint { get; }

    /// <summary>The container name.</summary>
    public string Container { get; }

    /// <summary>The credential to sign a SAS with.</summary>
    public StorageSharedKeyCredential CreateCredential()
    {
        return new StorageSharedKeyCredential(AccountName, _accountKey);
    }

    /// <summary>
    ///     Resolves the account from <paramref name="options" /> with the precedence of
    ///     <see cref="AzureBlobArtifactStore.CreateContainerClient" />: connection string, then account URL + key.
    /// </summary>
    /// <param name="options">The Azure Blob settings.</param>
    /// <param name="account">The account when the options allow a SAS.</param>
    /// <param name="unsupportedReason">Why not, when <paramref name="account" /> is <c>null</c>.</param>
    public static bool TryResolve(AzureBlobArtifactStorageOptions options, out AzureStagingAccount? account,
        out string? unsupportedReason)
    {
        account = null;
        unsupportedReason = null;

        if (string.IsNullOrWhiteSpace(options.Container))
        {
            unsupportedReason = $"{ArtifactStorageOptions.SectionName}:AzureBlob:Container is not set.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return TryResolveFromConnectionString(options, out account, out unsupportedReason);
        }

        if (string.IsNullOrWhiteSpace(options.AccountKey))
        {
            unsupportedReason = NoAccountKeyReason(options.UseManagedIdentity
                ? "the store authenticates with a managed identity"
                : "no account key is configured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.AccountUrl) ||
            !Uri.TryCreate(options.AccountUrl, UriKind.Absolute, out var accountUri))
        {
            unsupportedReason = $"{ArtifactStorageOptions.SectionName}:AzureBlob:AccountUrl is not an absolute URL.";
            return false;
        }

        var accountName = new BlobUriBuilder(accountUri).AccountName;
        if (string.IsNullOrEmpty(accountName))
        {
            unsupportedReason = "The storage account name cannot be derived from AccountUrl.";
            return false;
        }

        account = new AzureStagingAccount(accountName, options.AccountKey, TrimTrailingSlash(accountUri),
            options.Container);
        return true;
    }

    /// <summary>Returns the account and container only, never the key.</summary>
    public override string ToString()
    {
        return $"{AccountName}/{Container}";
    }

    private static bool TryResolveFromConnectionString(AzureBlobArtifactStorageOptions options,
        out AzureStagingAccount? account, out string? unsupportedReason)
    {
        account = null;
        unsupportedReason = null;
        var settings = ParseConnectionString(options.ConnectionString!);

        if (!settings.TryGetValue("AccountKey", out var key) || string.IsNullOrWhiteSpace(key))
        {
            unsupportedReason = NoAccountKeyReason("the connection string has no AccountKey");
            return false;
        }

        if (!settings.TryGetValue("AccountName", out var name) || string.IsNullOrWhiteSpace(name))
        {
            unsupportedReason = "The connection string has no AccountName.";
            return false;
        }

        Uri endpoint;
        if (settings.TryGetValue("BlobEndpoint", out var blobEndpoint) && !string.IsNullOrWhiteSpace(blobEndpoint))
        {
            if (!Uri.TryCreate(blobEndpoint, UriKind.Absolute, out var parsed))
            {
                unsupportedReason = "The connection string has no usable BlobEndpoint.";
                return false;
            }

            endpoint = TrimTrailingSlash(parsed);
        }
        else
        {
            settings.TryGetValue("DefaultEndpointsProtocol", out var protocol);
            settings.TryGetValue("EndpointSuffix", out var suffix);
            endpoint = new Uri($"{(string.IsNullOrWhiteSpace(protocol) ? "https" : protocol)}://{name}.blob." +
                               $"{(string.IsNullOrWhiteSpace(suffix) ? "core.windows.net" : suffix)}");
        }

        account = new AzureStagingAccount(name, key, endpoint, options.Container!);
        return true;
    }

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0)
            {
                settings[part[..separator].Trim()] = part[(separator + 1)..].Trim();
            }
        }

        return settings;
    }

    private static Uri TrimTrailingSlash(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(text);
    }

    private static string NoAccountKeyReason(string detail)
    {
        return $"A container SAS for the archive copy needs the storage account key, but {detail}. " +
               "Authentication without an account key (managed identity, user delegation SAS) is phase 2; " +
               "the dump and restore run as format 1 (rows through the service).";
    }

    /// <summary>The SAS protocol that matches the endpoint: HTTPS only, HTTP too only for an emulator endpoint.</summary>
    public SasProtocol SasProtocol => BlobEndpoint.Scheme == Uri.UriSchemeHttps
        ? SasProtocol.Https
        : SasProtocol.HttpsAndHttp;
}
