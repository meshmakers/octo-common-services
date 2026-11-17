using System.Xml.Linq;

namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>
///     Rewrites the XML of an ASP.NET Data Protection key into the form ASP.NET itself writes when a key encryptor is
///     configured: every element marked <c>requiresEncryption="true"</c> (the <c>masterKey</c>) is replaced by
///     <c>&lt;encryptedSecret decryptorType="…"&gt;&lt;platformKeyRingEncryptedKey&gt;…&lt;/encryptedSecret&gt;</c>.
/// </summary>
/// <remarks>
///     The key store writes the encrypted form itself (instead of <c>KeyManagementOptions.XmlEncryptor</c>) so that
///     <c>decryptorType</c> carries no assembly version: a key written by a newer image stays readable after a rollback
///     to an older image that already ships the decryptor.
/// </remarks>
public static class DataProtectionKeyXml
{
    /// <summary>CK type of a stored key element (system tenant, shared with the identity key ring).</summary>
    public const string KeyCkTypeId = "System.Identity/DataProtectionKey";

    /// <summary>Attribute that carries the element XML.</summary>
    public const string XmlDataAttributeName = "XmlData";

    /// <summary>Attribute that carries the stored element name (unique).</summary>
    public const string FriendlyNameAttributeName = "FriendlyName";

    /// <summary>Attribute that carries the time the element was stored.</summary>
    public const string CreationDateTimeAttributeName = "CreationDateTime";

    /// <summary>Root namespace of the ASP.NET Data Protection key XML.</summary>
    public const string DataProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>
    ///     Version-free type name of <see cref="PlatformKeyRingXmlDecryptor" /> as written into <c>decryptorType</c>.
    ///     Never rename or move the decryptor: stored keys reference this name.
    /// </summary>
    public const string DecryptorTypeName =
        "Meshmakers.Octo.Services.Infrastructure.DataProtection.PlatformKeyRingXmlDecryptor, " +
        "Meshmakers.Octo.Services.Infrastructure";

    /// <summary><c>{dataProtection}encryptedSecret</c></summary>
    public static readonly XName EncryptedSecretElementName = XName.Get("encryptedSecret", DataProtectionNamespace);

    /// <summary><c>{dataProtection}requiresEncryption</c></summary>
    public static readonly XName RequiresEncryptionAttributeName =
        XName.Get("requiresEncryption", DataProtectionNamespace);

    /// <summary><c>decryptorType</c> (no namespace)</summary>
    public static readonly XName DecryptorTypeAttributeName = "decryptorType";

    /// <summary>True when the key contains a secret in clear text (an element marked <c>requiresEncryption</c>).</summary>
    public static bool ContainsUnencryptedSecret(XElement key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return FindUnencryptedSecrets(key).Any();
    }

    /// <summary>
    ///     Returns a copy of <paramref name="key" /> with every clear-text secret wrapped by
    ///     <paramref name="encryptor" />. A key without clear-text secrets is returned as an unchanged copy.
    /// </summary>
    /// <exception cref="Meshmakers.Octo.Runtime.Contracts.Secrets.SecretEncryptionNotConfiguredException">No active platform key</exception>
    public static XElement EncryptSecrets(XElement key, PlatformKeyRingXmlEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(encryptor);

        var copy = new XElement(key);
        foreach (var secret in FindUnencryptedSecrets(copy).ToList())
        {
            var encrypted = encryptor.Encrypt(new XElement(secret));
            secret.ReplaceWith(new XElement(EncryptedSecretElementName,
                new XAttribute(DecryptorTypeAttributeName, DecryptorTypeName),
                encrypted.EncryptedElement));
        }

        // ASP.NET adds "<!-- Warning: the key below is in an unencrypted form. -->" to keys written without an
        // encryptor; it is wrong once the secret is wrapped.
        foreach (var comment in copy.DescendantNodes().OfType<XComment>()
                     .Where(c => c.Value.Contains("unencrypted form", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            comment.Remove();
        }

        return copy;
    }

    private static IEnumerable<XElement> FindUnencryptedSecrets(XElement key)
    {
        return key.DescendantsAndSelf()
            .Where(e => string.Equals((string?)e.Attribute(RequiresEncryptionAttributeName), "true",
                            StringComparison.OrdinalIgnoreCase)
                        && !e.Ancestors(EncryptedSecretElementName).Any());
    }
}
