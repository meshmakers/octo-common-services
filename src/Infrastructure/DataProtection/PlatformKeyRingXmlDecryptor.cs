using System.Xml.Linq;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>
///     Reverses <see cref="PlatformKeyRingXmlEncryptor" />. ASP.NET Data Protection activates it from the
///     <c>decryptorType</c> attribute of a stored key (<see cref="DataProtectionKeyXml.DecryptorTypeName" />), through
///     the constructor that takes an <see cref="IServiceProvider" />.
/// </summary>
/// <remarks>
///     Any key id of the platform key ring decrypts, not only the active one, so a rotation of
///     <c>SecretEncryption:ActiveKeyId</c> with the old key kept leaves every stored key readable. A missing key id, a
///     tampered envelope or a wrong key throws; ASP.NET then ignores that key and creates a new one.
///     Never rename or move this type: stored keys reference it by name.
/// </remarks>
public sealed class PlatformKeyRingXmlDecryptor : IXmlDecryptor
{
    private static readonly SecretAccessContext AccessContext =
        new(CkTypeId: DataProtectionKeyXml.KeyCkTypeId, AttributeName: DataProtectionKeyXml.XmlDataAttributeName);

    private readonly Lazy<ISecretAttributeProtector> _secretProtector;

    /// <summary>Constructor used by the Data Protection activator.</summary>
    public PlatformKeyRingXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _secretProtector = new Lazy<ISecretAttributeProtector>(services.GetRequiredService<ISecretAttributeProtector>);
    }

    /// <summary>Constructor for direct use (tests, maintenance).</summary>
    public PlatformKeyRingXmlDecryptor(ISecretAttributeProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(secretProtector);
        _secretProtector = new Lazy<ISecretAttributeProtector>(secretProtector);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The element is not a platform key ring envelope</exception>
    /// <exception cref="UnknownSecretKeyIdException">The envelope's key id is not in the platform key ring</exception>
    /// <exception cref="SecretEncryptionNotConfiguredException">No platform key ring is configured</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Tampered envelope or wrong key</exception>
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var envelope = GetEnvelope(encryptedElement);
        var protector = _secretProtector.Value;
        if (!protector.TryParseEnvelope(envelope, out var info) || info.Version != 2)
        {
            throw new InvalidOperationException(
                "The Data Protection key element does not contain a platform key ring envelope (enc:v2).");
        }

        return XElement.Parse(protector.Unprotect(envelope, AccessContext));
    }

    /// <summary>Returns the envelope of an element written by <see cref="PlatformKeyRingXmlEncryptor" />.</summary>
    /// <exception cref="InvalidOperationException">The element has another shape</exception>
    internal static string GetEnvelope(XElement encryptedElement)
    {
        if (encryptedElement.Name != PlatformKeyRingXmlEncryptor.EncryptedElementName)
        {
            throw new InvalidOperationException(
                $"Unexpected Data Protection key element '{encryptedElement.Name.LocalName}'; expected " +
                $"'{PlatformKeyRingXmlEncryptor.EncryptedElementName.LocalName}'.");
        }

        var value = encryptedElement.Element(PlatformKeyRingXmlEncryptor.ValueElementName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The Data Protection key element has no envelope value.");
        }

        return value.Trim();
    }
}
