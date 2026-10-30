using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>State of an idempotency record (OD-4).</summary>
public enum CkMethodIdempotencyState
{
    /// <summary>The first call with the key is running (until <see cref="CkMethodIdempotencyRecord.LeaseUntil" />).</summary>
    InProgress,

    /// <summary>The first call ended and its response envelope is stored; a repeat replays it.</summary>
    Completed,

    /// <summary>
    ///     The outcome of the first call is not known (timeout, outcome unknown, or the lease of an in-flight claim lapsed).
    ///     The key is <b>burned</b> (OD-4 row 4): a repeat is neither executed nor answered with a result.
    /// </summary>
    Unknown
}

/// <summary>
///     One idempotency record. Immutable; the stores replace records, they never edit them. Equality is by <b>reference</b>:
///     a conditional replace ("only if it is still the record I read") must compare identity, not content.
/// </summary>
public sealed record CkMethodIdempotencyRecord
{
    /// <summary>Primary key: <see cref="CkMethodIdempotencyKey.Id" />.</summary>
    public required string Id { get; init; }

    /// <summary>The stored state. Use <see cref="ResolveState" /> to read it at a point in time.</summary>
    public required CkMethodIdempotencyState State { get; init; }

    /// <summary>Qualified method id of the first call.</summary>
    public required string MethodId { get; init; }

    /// <summary>Invocation id of the call that took the claim. Only that call may complete or release it.</summary>
    public required Guid FirstInvocationId { get; init; }

    /// <summary>
    ///     Keyed fingerprint (HMAC, OD-4 G1) of the parameters of the first call. Compared in constant time by the filter;
    ///     never logged. Empty when the call carried no fingerprint.
    /// </summary>
    public ReadOnlyMemory<byte> Fingerprint { get; init; }

    /// <summary>Id of the key that produced <see cref="Fingerprint" /> (never the key), for rotation.</summary>
    public string? FingerprintKeyId { get; init; }

    /// <summary>When the claim was taken (UTC).</summary>
    public required DateTime ClaimedAt { get; init; }

    /// <summary>
    ///     End of the in-flight window (UTC): the request deadline plus the response grace of the dispatcher. An
    ///     <see cref="CkMethodIdempotencyState.InProgress" /> record past it is <see cref="CkMethodIdempotencyState.Unknown" />.
    /// </summary>
    public required DateTime LeaseUntil { get; init; }

    /// <summary>
    ///     End of the lifetime of the record (UTC); counts from <see cref="ClaimedAt" />, completing does not extend it.
    ///     Readers check it themselves: an expired record is absent, whether or not housekeeping removed it.
    /// </summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>
    ///     The stored response envelope of a <see cref="CkMethodIdempotencyState.Completed" /> record. Keys of methods whose
    ///     result is sensitive are refused (OD-4 G2), so nothing sensitive is stored here.
    /// </summary>
    public CkMethodResponse? Response { get; init; }

    /// <inheritdoc />
    public bool Equals(CkMethodIdempotencyRecord? other) => ReferenceEquals(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>Whether the record is past its lifetime at <paramref name="utcNow" />.</summary>
    public bool IsExpired(DateTime utcNow) => ExpiresAt <= utcNow;

    /// <summary>
    ///     The state at <paramref name="utcNow" />: an in-flight claim whose lease lapsed is
    ///     <see cref="CkMethodIdempotencyState.Unknown" /> (OD-4 row 3/4), every other state is as stored.
    /// </summary>
    public CkMethodIdempotencyState ResolveState(DateTime utcNow) =>
        State == CkMethodIdempotencyState.InProgress && LeaseUntil <= utcNow
            ? CkMethodIdempotencyState.Unknown
            : State;

    /// <summary>Prints id, state and expiry only: no fingerprint, no response.</summary>
    public override string ToString() =>
        $"{nameof(CkMethodIdempotencyRecord)} {{ Id = {Id}, State = {State}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>What a caller hands the store to take a claim.</summary>
/// <param name="Key">The identity of the key.</param>
/// <param name="FirstInvocationId">Invocation id of the calling invocation; becomes the owner of the claim.</param>
/// <param name="LeaseUntil">End of the in-flight window (UTC): request deadline plus response grace.</param>
/// <param name="Fingerprint">Keyed fingerprint of the raw parameters (may be empty).</param>
/// <param name="FingerprintKeyId">Id of the fingerprint key.</param>
public sealed record CkMethodIdempotencyClaimRequest(
    CkMethodIdempotencyKey Key,
    Guid FirstInvocationId,
    DateTime LeaseUntil,
    ReadOnlyMemory<byte> Fingerprint = default,
    string? FingerprintKeyId = null)
{
    /// <summary>Prints no fingerprint.</summary>
    public override string ToString() =>
        $"{nameof(CkMethodIdempotencyClaimRequest)} {{ Key = {Key}, FirstInvocationId = {FirstInvocationId} }}";
}

/// <summary>Result of <see cref="ICkMethodIdempotencyStore.ClaimAsync" />: this call took the claim, or a live record exists.</summary>
public sealed class CkMethodIdempotencyClaimResult
{
    private CkMethodIdempotencyClaimResult(bool claimed, CkMethodIdempotencyRecord record)
    {
        Claimed = claimed;
        Record = record;
    }

    /// <summary><c>true</c>: this call owns the claim and must complete or release it.</summary>
    public bool Claimed { get; }

    /// <summary>The new record when <see cref="Claimed" />, else the existing live record (never an expired one).</summary>
    public CkMethodIdempotencyRecord Record { get; }

    /// <summary>The call took the claim.</summary>
    public static CkMethodIdempotencyClaimResult ClaimedBy(CkMethodIdempotencyRecord record) => new(true, record);

    /// <summary>A live record of an earlier call exists.</summary>
    public static CkMethodIdempotencyClaimResult ExistingRecord(CkMethodIdempotencyRecord record) => new(false, record);
}

/// <summary>
///     The store behind the idempotency key (OD-4; AB#7817). The contract only: the filter that uses it is a later story and
///     the MongoDB implementation lives where the tenant database provider is (this library keeps no MongoDB reference).
///     <para>
///         <b>Fails closed</b> (OD-4 row 6): an implementation that cannot decide for certain (database unreachable,
///         timeout, an unexpected write result) <b>throws</b>. It never reports <see cref="CkMethodIdempotencyClaimResult.Claimed" />
///         on a guess and never swallows a failure; the caller refuses the call, the method is not executed.
///     </para>
///     <para>
///         Which outcomes release or burn a claim (OD-4 row 4): only a call that was <b>not executed</b> may
///         <see cref="ReleaseAsync" /> its claim. An unknown outcome (timeout, outcome unknown, a lapsed in-flight claim) and
///         <c>METHOD_HANDLER_FAILED</c> burn the key: <see cref="CompleteAsync" /> with
///         <see cref="CkMethodIdempotencyState.Unknown" /> or a stored failure envelope; the caller uses a new key.
///     </para>
/// </summary>
public interface ICkMethodIdempotencyStore
{
    /// <summary>
    ///     Takes the claim of <see cref="CkMethodIdempotencyClaimRequest.Key" /> when no live record exists, atomically:
    ///     of any number of concurrent calls exactly one gets <see cref="CkMethodIdempotencyClaimResult.Claimed" />. A record
    ///     past its <see cref="CkMethodIdempotencyRecord.ExpiresAt" /> counts as absent and is taken over with a conditional
    ///     replace (only if it is still the record that was read), so two takers never both win. Otherwise the existing live
    ///     record is returned.
    /// </summary>
    /// <param name="request">The claim.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="CkMethodIdempotencyStoreException">The store could not decide (fail closed).</exception>
    Task<CkMethodIdempotencyClaimResult> ClaimAsync(CkMethodIdempotencyClaimRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stores the outcome of the call that owns the claim. Only the owner (<paramref name="firstInvocationId" />) can
    ///     complete, and only while the record is <see cref="CkMethodIdempotencyState.InProgress" />; the lifetime
    ///     (<see cref="CkMethodIdempotencyRecord.ExpiresAt" />) does not change.
    /// </summary>
    /// <param name="id"><see cref="CkMethodIdempotencyKey.Id" />.</param>
    /// <param name="firstInvocationId">Invocation id of the owner.</param>
    /// <param name="state"><see cref="CkMethodIdempotencyState.Completed" /> or <see cref="CkMethodIdempotencyState.Unknown" />.</param>
    /// <param name="response">The envelope to replay; required for <c>Completed</c>, ignored for <c>Unknown</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when stored; <c>false</c> when the caller is not the owner, the record is gone or no longer in progress.</returns>
    /// <exception cref="ArgumentException"><paramref name="state" /> is <c>InProgress</c>, or <c>Completed</c> without a response.</exception>
    /// <exception cref="CkMethodIdempotencyStoreException">The store could not decide (fail closed).</exception>
    Task<bool> CompleteAsync(string id, Guid firstInvocationId, CkMethodIdempotencyState state,
        CkMethodResponse? response, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gives the claim back because the call was <b>not executed</b> (a not-executed platform code), so the same key may
    ///     be used again at once. Only the owner can release, and only while the record is
    ///     <see cref="CkMethodIdempotencyState.InProgress" />.
    /// </summary>
    /// <param name="id"><see cref="CkMethodIdempotencyKey.Id" />.</param>
    /// <param name="firstInvocationId">Invocation id of the owner.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns><c>true</c> when released; <c>false</c> when the caller is not the owner or the record is gone or settled.</returns>
    /// <exception cref="CkMethodIdempotencyStoreException">The store could not decide (fail closed).</exception>
    Task<bool> ReleaseAsync(string id, Guid firstInvocationId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The store could not decide whether the key is free (database unreachable, timeout, unexpected result). The caller
///     refuses the call; the method is not executed (fail closed).
/// </summary>
public sealed class CkMethodIdempotencyStoreException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CkMethodIdempotencyStoreException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CkMethodIdempotencyStoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
