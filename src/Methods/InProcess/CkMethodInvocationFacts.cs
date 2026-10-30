using System.Security.Cryptography;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     The opaque keyed fingerprint of the parameters of one invocation (OD-4 row 2, G1: a keyed HMAC over type, target and
///     the raw parameters, computed by the pipeline before redaction). It exists only inside the process of one invocation:
///     it is never serialized, never logged, never audited. <see cref="ToString" /> prints no value.
/// </summary>
public sealed class CkMethodParametersFingerprint
{
    private readonly byte[] _value;

    /// <summary>Creates a fingerprint.</summary>
    /// <param name="value">The HMAC bytes (copied).</param>
    /// <param name="keyId">Id of the key that produced it (never the key itself), so a rotated key can be told apart.</param>
    public CkMethodParametersFingerprint(ReadOnlySpan<byte> value, string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (value.IsEmpty)
        {
            throw new ArgumentException("A fingerprint needs a value.", nameof(value));
        }

        _value = value.ToArray();
        KeyId = keyId;
    }

    /// <summary>Id of the key that produced the fingerprint.</summary>
    public string KeyId { get; }

    /// <summary>A copy of the fingerprint bytes, for the store that persists them. Do not log it.</summary>
    public byte[] ToArray() => (byte[])_value.Clone();

    /// <summary>Compares in constant time. Fingerprints of different keys are never equal.</summary>
    public bool Matches(CkMethodParametersFingerprint? other) =>
        other != null && string.Equals(KeyId, other.KeyId, StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(_value, other._value);

    /// <summary>Never prints the value.</summary>
    public override string ToString() => $"{nameof(CkMethodParametersFingerprint)} {{ KeyId = {KeyId}, Value = [redacted] }}";
}

/// <summary>
///     Facts about <b>one</b> invocation that the filters of the pipeline share (AB#7816): an inner filter tells the outer
///     ones what it did without the filters knowing each other (the idempotency filter at band 300 tells the audit filter at
///     band 100 that it answered from its store). The pipeline creates one instance per call and puts it on the
///     <see cref="CkMethodInvocationContext" />; every filter of that call sees the same instance, two calls never share
///     one. It is not part of <see cref="Contracts.DistributionEventHub.Methods.CkMethodRequest" /> or
///     <see cref="Contracts.DistributionEventHub.Methods.CkMethodResponse" />, never goes on the wire and is never serialized.
///     A filter reads the facts of an inner filter after <c>next()</c> returned.
///     <para>
///         Who sets what: <see cref="ReplayOf" /> is set by the idempotency filter (band 300) and by nobody else;
///         <see cref="ParametersFingerprint" /> is set by the pipeline before the first filter runs (internal setter) and is
///         read-only for filters.
///     </para>
/// </summary>
public sealed class CkMethodInvocationFacts
{
    /// <summary>
    ///     The first invocation id of the idempotency key when this call was answered from the store instead of
    ///     executing the method (a replay); <c>null</c> when the method was executed or the call has no key. Set by the
    ///     idempotency filter.
    /// </summary>
    public Guid? ReplayOf { get; set; }

    /// <summary>
    ///     The keyed fingerprint of the raw parameters, computed by the pipeline before redaction (AB#7819), or
    ///     <c>null</c> when none was computed (no idempotency key, or the feature is off). Read-only for filters.
    /// </summary>
    public CkMethodParametersFingerprint? ParametersFingerprint { get; internal set; }

    /// <summary>
    ///     Fingerprints of the same raw request under other keys that a stored fingerprint may come from (AB#7819): the
    ///     previous key of a rotation, or the unkeyed SHA-256 made before a key was configured. Empty when there are none.
    ///     Read-only for filters; use <see cref="MatchesStoredFingerprint" /> instead of comparing yourself.
    /// </summary>
    public IReadOnlyList<CkMethodParametersFingerprint> AlternateParametersFingerprints { get; internal set; } = [];

    /// <summary>
    ///     <c>true</c> when <paramref name="stored" /> (read from the record of the first call, with its key id) is the
    ///     fingerprint of this request under the key that made it: the current key, the previous key after a rotation, or
    ///     the unkeyed SHA-256. <c>false</c> when the parameters differ, the key id is unknown, or this call has no
    ///     fingerprint. Constant-time comparison.
    /// </summary>
    /// <param name="stored">The stored fingerprint (bytes and key id).</param>
    public bool MatchesStoredFingerprint(CkMethodParametersFingerprint? stored)
    {
        if (stored == null || ParametersFingerprint == null)
        {
            return false;
        }

        if (ParametersFingerprint.Matches(stored))
        {
            return true;
        }

        foreach (var alternate in AlternateParametersFingerprints)
        {
            if (alternate.Matches(stored))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Prints the replay id and whether a fingerprint exists, never the fingerprint.</summary>
    public override string ToString() =>
        $"{nameof(CkMethodInvocationFacts)} {{ ReplayOf = {ReplayOf?.ToString() ?? "none"}, " +
        $"ParametersFingerprint = {(ParametersFingerprint == null ? "none" : "[redacted]")} }}";
}
