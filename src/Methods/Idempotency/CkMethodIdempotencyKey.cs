using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     The identity of an idempotency key (OD-4 row 1): tenant + qualified method id + caller scope + the key the caller
///     sent. The caller scope comes from <c>ICkMethodPrincipalProvider</c> through <see cref="ScopeOf" /> (the <c>sub</c> of
///     a user, <c>client:&lt;client_id&gt;</c> of a service token, AB#8078), <b>never</b> from <c>CkMethodRequest.Caller</c>,
///     which is informational. The target is deliberately not part of the
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

    /// <summary>
    ///     The prefix of the scope of a client-credentials caller (<c>client:&lt;client_id&gt;</c>, AB#8078). It keeps the
    ///     scope of a service token apart from the scope of a user whose <c>sub</c> is the same string.
    /// </summary>
    public const string ClientScopePrefix = "client:";

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

    /// <summary>The authenticated caller scope (see <see cref="ScopeOf" />).</summary>
    public string Subject { get; }

    /// <summary>Lower-case hex SHA-256 of the four parts; 64 characters. The only form of the key that is stored.</summary>
    public string Id { get; }

    /// <summary>
    ///     The idempotency scope of a caller (decided by Odin in AB#7829, AB#8078): a user is scoped by its <c>sub</c>
    ///     (<see cref="CkMethodPrincipal.SubjectId" />); a client-credentials caller (no <c>sub</c>) by
    ///     <c>client:&lt;client_id&gt;</c>, so a user id and a client id never share a scope. The audit keeps the plain
    ///     <see cref="CkMethodPrincipal.Subject" />; only the idempotency scope carries the prefix.
    /// </summary>
    /// <param name="principal">The trusted principal of the call, or <c>null</c>.</param>
    /// <returns>The scope, or <c>null</c> when the caller has neither a subject nor a client id (no scope).</returns>
    public static string? ScopeOf(CkMethodPrincipal? principal)
    {
        if (principal == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(principal.SubjectId))
        {
            return principal.SubjectId;
        }

        return string.IsNullOrWhiteSpace(principal.ClientId) ? null : ClientScopePrefix + principal.ClientId;
    }

    /// <summary>Builds the identity.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="methodId">The qualified method id.</param>
    /// <param name="subject">
    ///     The authenticated caller scope (<see cref="ScopeOf" />); a call without one has no idempotency scope.
    /// </param>
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
