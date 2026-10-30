using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     The identity of an idempotency key (OD-4 row 1): tenant + qualified method id + caller subject + the key the caller
///     sent. The caller subject comes from <c>ICkMethodPrincipalProvider</c> (sub, else client_id of a service token),
///     <b>never</b> from <c>CkMethodRequest.Caller</c>, which is informational. The target is deliberately not part of the
///     identity: a key names one operation of one caller; the target is part of the parameter fingerprint.
///     <para>
///         <see cref="Id" /> is the SHA-256 of the four parts (each length-prefixed, so no value can shift into its
///         neighbour). It is the primary key of the store; the raw key is not kept at rest.
///     </para>
/// </summary>
public sealed class CkMethodIdempotencyKey
{
    /// <summary>The documented maximum length of a key (<c>CkMethodRequest.IdempotencyKey</c>).</summary>
    public const int MaxKeyLength = 128;

    private CkMethodIdempotencyKey(string tenantId, string methodId, string subject, string id)
    {
        TenantId = tenantId;
        MethodId = methodId;
        Subject = subject;
        Id = id;
    }

    /// <summary>The tenant.</summary>
    public string TenantId { get; }

    /// <summary>The qualified method id.</summary>
    public string MethodId { get; }

    /// <summary>The authenticated caller subject.</summary>
    public string Subject { get; }

    /// <summary>Lower-case hex SHA-256 of the four parts; 64 characters. The only form of the key that is stored.</summary>
    public string Id { get; }

    /// <summary>Builds the identity.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="methodId">The qualified method id.</param>
    /// <param name="subject">The authenticated caller subject; a call without one has no idempotency scope.</param>
    /// <param name="key">The key the caller sent: 1 to <see cref="MaxKeyLength" /> characters.</param>
    /// <exception cref="ArgumentException">A part is empty or the key is longer than <see cref="MaxKeyLength" />.</exception>
    public static CkMethodIdempotencyKey Create(string tenantId, string methodId, string subject, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException($"An idempotency key has at most {MaxKeyLength} characters.", nameof(key));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, tenantId);
        Append(hash, methodId);
        Append(hash, subject);
        Append(hash, key);
        return new CkMethodIdempotencyKey(tenantId, methodId, subject, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Prints the id, never the raw key (which is not kept).</summary>
    public override string ToString() => $"{nameof(CkMethodIdempotencyKey)} {{ Id = {Id} }}";

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
