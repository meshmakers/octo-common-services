using System.Xml.Linq;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>
///     ASP.NET Data Protection key ring of one service, persisted in the shared store
///     (<see cref="IDataProtectionKeyRecordStore" />) so every replica uses the same keys and the keys survive restarts.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Only the elements of this service are read and written (stored name
///         <c>&lt;applicationName&gt;/&lt;friendlyName&gt;</c>, <see cref="DataProtectionKeyNames" />); other services and
///         the identity service keep their own key rings in the same store.</item>
///         <item>The secret part of every new key is wrapped with the platform key ring (<c>SecretEncryption</c>,
///         <see cref="DataProtectionKeyXml.EncryptSecrets" />) before it is stored. Without a key ring nothing is
///         stored (the registration then does not use this repository at all).</item>
///         <item><see cref="IXmlRepository" /> is synchronous by contract; the rare calls (key ring load, key
///         creation about every 90 days) make sync-over-async acceptable here.</item>
///     </list>
///     No key material is ever logged.
/// </remarks>
internal sealed class OctoDataProtectionKeyRepository : IXmlRepository
{
    private readonly string _applicationName;
    private readonly PlatformKeyRingXmlEncryptor _encryptor;
    private readonly ILogger<OctoDataProtectionKeyRepository> _logger;
    private readonly IDataProtectionKeyRecordStore _store;

    public OctoDataProtectionKeyRepository(OctoDataProtectionKeyRingOptions options, IDataProtectionKeyRecordStore store,
        ISecretAttributeProtector secretProtector, ILogger<OctoDataProtectionKeyRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretProtector);
        DataProtectionKeyNames.ValidateApplicationName(options.ApplicationName);

        _applicationName = options.ApplicationName;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _encryptor = new PlatformKeyRingXmlEncryptor(secretProtector);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        return GetAllElementsAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        var name = string.IsNullOrWhiteSpace(friendlyName) ? $"key-{Guid.NewGuid():D}" : friendlyName;
        StoreElementAsync(element, DataProtectionKeyNames.ToStoredName(_applicationName, name)).GetAwaiter()
            .GetResult();
    }

    private async Task<IReadOnlyCollection<XElement>> GetAllElementsAsync()
    {
        var records = await _store.LoadAllAsync();
        var elements = new List<XElement>();
        foreach (var record in records.Where(r => DataProtectionKeyNames.BelongsTo(_applicationName, r.StoredName)))
        {
            try
            {
                elements.Add(XElement.Parse(record.XmlData));
            }
            catch (System.Xml.XmlException ex)
            {
                // One broken element must not take the whole key ring down; ASP.NET creates a new key if needed.
                _logger.LogError("Data Protection key '{StoredName}' is not valid XML ({Message}); it is ignored",
                    record.StoredName, ex.Message);
            }
        }

        return elements;
    }

    private async Task StoreElementAsync(XElement element, string storedName)
    {
        if (!await _store.CanPersistAsync())
        {
            // ASP.NET preloads the key ring when the host starts, which can be before the system tenant exists on a
            // fresh installation. The failed write is not fatal there; the key is created again on first real use.
            throw new InvalidOperationException(
                $"Data Protection key '{storedName}' cannot be persisted before the system tenant exists.");
        }

        await _store.InsertAsync(new DataProtectionKeyRecord(storedName,
            PrepareForStorage(element, storedName).ToString(SaveOptions.DisableFormatting)));
    }

    private XElement PrepareForStorage(XElement element, string storedName)
    {
        if (!DataProtectionKeyXml.ContainsUnencryptedSecret(element))
        {
            return element;
        }

        if (!_encryptor.IsConfigured)
        {
            // The registration does not use this repository without a key ring; never fall back to clear text.
            throw new InvalidOperationException(
                $"Data Protection key '{storedName}' is not stored: no SecretEncryption key ring is configured " +
                "(OCTO_SECRETENCRYPTION__KEYS__<id>, OCTO_SECRETENCRYPTION__ACTIVEKEYID) and keys are never stored " +
                "unencrypted in the shared store.");
        }

        var encrypted = DataProtectionKeyXml.EncryptSecrets(element, _encryptor);
        _logger.LogInformation("Data Protection key '{StoredName}' is stored encrypted with platform key id '{KeyId}'",
            storedName, _encryptor.ActiveKeyId);
        return encrypted;
    }
}
