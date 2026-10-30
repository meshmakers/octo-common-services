using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     The idempotency filter of the invocation pipeline (band <see cref="CkMethodInvocationFilterOrder.Idempotency" />,
///     AB#7820, decided in OD-4 AB#6922). A call that carries <c>IdempotencyKey</c> runs at most once per
///     (tenant, method, caller subject, key): the first call claims the key, executes and stores its outcome; a repeat is
///     answered from the store and never reaches the handler. A call without a key passes straight through (no store
///     access, no allocation).
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Position:</b> inside authorization (an unauthorized caller learns nothing about stored keys) and behind the
///                 fingerprint refusal of the pipeline. The audit filter (100) sees every outcome; a replay sets
///                 <see cref="CkMethodInvocationFacts.ReplayOf" />.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Scope:</b> the caller subject comes from <see cref="ICkMethodPrincipalProvider" />, never from
///                 <c>CkMethodRequest.Caller</c>. A call without a subject is refused, not executed unkeyed.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Same key:</b> other parameters (or a stored fingerprint that cannot be verified) are refused with
///                 <c>METHOD_VALIDATION_FAILED</c> / <c>parameters-changed</c>; while the first call runs the answer is
///                 <c>METHOD_REQUEST_IN_PROGRESS</c>; an unknown first outcome is <c>METHOD_OUTCOME_UNKNOWN</c> (the key is burned).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Outcomes:</b> only a not-executed platform code releases the claim (the same key may be used at once).
///                 A timeout, an unknown outcome, an exception or cancellation burn the key (state <c>Unknown</c>). Everything
///                 else (success, accepted, <c>METHOD_HANDLER_FAILED</c>, a code the method declares) is stored and replayed:
///                 for a failure the key is burned in the sense that the caller needs a new key to try again.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Sensitive results:</b> a method whose result is sensitive (or unknown) refuses a key
///                 (<c>result-sensitive</c>); nothing is stored for it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Fail closed:</b> when the store cannot decide, the call is not executed
///                 (<c>METHOD_HANDLER_NOT_AVAILABLE</c>, reason <c>idempotency-store-unavailable</c>).
///             </description>
///         </item>
///     </list>
///     The log names the method, invocation id and the opaque key id (a hash); never the raw key, a fingerprint, a
///     parameter or a result.
/// </remarks>
public sealed class CkMethodIdempotencyFilter(
    ICkMethodIdempotencyStore store,
    ICkMethodPrincipalProvider principalProvider,
    ICkMethodSensitivityResolver sensitivity,
    IOptions<CkMethodDispatcherOptions> dispatcherOptions,
    TimeProvider timeProvider,
    ILogger<CkMethodIdempotencyFilter> logger) : ICkMethodInvocationFilter
{
    /// <inheritdoc />
    public int Order => CkMethodInvocationFilterOrder.Idempotency;

    /// <inheritdoc />
    public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Request;
        if (string.IsNullOrEmpty(request.IdempotencyKey))
        {
            return await next(request).ConfigureAwait(false);
        }

        if (sensitivity.IsResultSensitive(request.TenantId, request.CkTypeId, request.MethodId))
        {
            return Refuse(request, CkMethodIdempotencyRefusal.ResultSensitive);
        }

        var subject = principalProvider.GetPrincipal(context)?.Subject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Refuse(request, CkMethodIdempotencyRefusal.CallerUnknown);
        }

        if (request.IdempotencyKey.Length > CkMethodIdempotencyKey.MaxKeyLength)
        {
            return Refuse(request, CkMethodIdempotencyRefusal.KeyTooLong);
        }

        var facts = context.Facts;
        var fingerprint = facts.ParametersFingerprint;
        if (fingerprint == null)
        {
            // the pipeline fingerprints every keyed call when the idempotency options are registered; without one the
            // key could be reused with other parameters
            logger.LogError(
                "CK method {MethodId} invocation {InvocationId}: idempotency key refused, no parameter fingerprint on the invocation (is AddCkMethodIdempotencyOptions registered?)",
                request.MethodId, request.InvocationId);
            return Refuse(request, CkMethodIdempotencyRefusal.FingerprintUnavailable);
        }

        var key = CkMethodIdempotencyKey.Create(request.TenantId, request.MethodId, subject, request.IdempotencyKey);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var grace = dispatcherOptions.Value.ResponseGrace > TimeSpan.Zero
            ? dispatcherOptions.Value.ResponseGrace
            : TimeSpan.Zero;
        var leaseUntil = (request.DeadlineUtc > now ? request.DeadlineUtc : now) + grace;

        CkMethodIdempotencyClaimResult claim;
        try
        {
            claim = await store.ClaimAsync(
                new CkMethodIdempotencyClaimRequest(key, request.InvocationId, leaseUntil, fingerprint.ToArray(),
                    fingerprint.KeyId), context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return StoreUnavailable(request, key, e);
        }

        if (!claim.Claimed)
        {
            return Answer(context, key, claim.Record, now);
        }

        return await ExecuteClaimedAsync(context, next, key).ConfigureAwait(false);
    }

    private CkMethodResponse Answer(CkMethodInvocationContext context, CkMethodIdempotencyKey key,
        CkMethodIdempotencyRecord record, DateTime now)
    {
        var request = context.Request;
        var facts = context.Facts;

        // Same key, other parameters: first, whatever the state is. A fingerprint that cannot be verified (empty, or made
        // with a key id that is no longer configured) never matches.
        var stored = record.Fingerprint.IsEmpty || string.IsNullOrWhiteSpace(record.FingerprintKeyId)
            ? null
            : new CkMethodParametersFingerprint(record.Fingerprint.Span, record.FingerprintKeyId);
        if (!facts.MatchesStoredFingerprint(stored))
        {
            logger.LogInformation(
                "CK method {MethodId} invocation {InvocationId} refused: idempotency key {KeyId} was used with other parameters",
                request.MethodId, request.InvocationId, key.Id);
            return Refuse(request, CkMethodIdempotencyRefusal.ParametersChanged);
        }

        switch (record.ResolveState(now))
        {
            case CkMethodIdempotencyState.InProgress:
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} not executed: the first call with idempotency key {KeyId} is still running",
                    request.MethodId, request.InvocationId, key.Id);
                return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                    CkMethodError.Create(CkMethodErrorCodes.MethodRequestInProgress,
                        (CkMethodErrorParameters.MethodId, request.MethodId)));

            case CkMethodIdempotencyState.Completed when record.Response is { } response:
                facts.ReplayOf = record.FirstInvocationId;
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} answered from the store (replay of {FirstInvocationId}, key {KeyId})",
                    request.MethodId, request.InvocationId, record.FirstInvocationId, key.Id);
                return response with { InvocationId = request.InvocationId };

            default:
                // Unknown, or a completed record without an envelope (which a store must not produce): burned
                logger.LogInformation(
                    "CK method {MethodId} invocation {InvocationId} not executed: the outcome of the first call with idempotency key {KeyId} is unknown, the key is burned",
                    request.MethodId, request.InvocationId, key.Id);
                return CkMethodResponse.Failed(request.InvocationId, string.Empty,
                    CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                        (CkMethodErrorParameters.MethodId, request.MethodId)));
        }
    }

    private async Task<CkMethodResponse> ExecuteClaimedAsync(CkMethodInvocationContext context,
        CkMethodInvocationDelegate next, CkMethodIdempotencyKey key)
    {
        var request = context.Request;
        CkMethodResponse response;
        try
        {
            response = await next(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller gave up or a filter/transport faulted: the method may have run, so the key is burned.
            // Settle without the caller's token (it may be the reason we are here).
            await SettleAsync(request, key, CkMethodIdempotencyState.Unknown, null).ConfigureAwait(false);
            throw;
        }

        switch (Classify(response))
        {
            case Outcome.NotExecuted:
                await ReleaseAsync(request, key).ConfigureAwait(false);
                break;
            case Outcome.Unknown:
                await SettleAsync(request, key, CkMethodIdempotencyState.Unknown, null).ConfigureAwait(false);
                break;
            default:
                await SettleAsync(request, key, CkMethodIdempotencyState.Completed, response).ConfigureAwait(false);
                break;
        }

        return response;
    }

    private enum Outcome
    {
        Store,
        NotExecuted,
        Unknown
    }

    private static Outcome Classify(CkMethodResponse response)
    {
        if (response.Success || response.Error == null)
        {
            return Outcome.Store;
        }

        var code = response.Error.Code;
        if (!CkMethodErrorCodes.IsPlatformCode(code))
        {
            return Outcome.Store; // a code the method declares: it ran and answered
        }

        var info = CkMethodErrorCodes.Catalog.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal));
        return info?.Execution switch
        {
            CkMethodExecutionState.NotExecuted => Outcome.NotExecuted,
            CkMethodExecutionState.Failed => Outcome.Store, // METHOD_HANDLER_FAILED: stored, the caller needs a new key
            _ => Outcome.Unknown // timeout, outcome unknown, a platform code this version does not know
        };
    }

    private async Task SettleAsync(CkMethodRequest request, CkMethodIdempotencyKey key, CkMethodIdempotencyState state,
        CkMethodResponse? response)
    {
        try
        {
            if (!await store.CompleteAsync(key.Id, request.InvocationId, state, response).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "CK method {MethodId} invocation {InvocationId}: the idempotency record {KeyId} could not be completed (no longer owned or not in progress)",
                    request.MethodId, request.InvocationId, key.Id);
            }
        }
        catch (Exception e)
        {
            // The call already ran; its answer must reach the caller. The record stays in progress and reads as
            // unknown once its lease lapsed, so the key is burned either way.
            logger.LogError(e,
                "CK method {MethodId} invocation {InvocationId}: the outcome could not be stored for idempotency key {KeyId}; the key stays in progress until its lease lapses",
                request.MethodId, request.InvocationId, key.Id);
        }
    }

    private async Task ReleaseAsync(CkMethodRequest request, CkMethodIdempotencyKey key)
    {
        try
        {
            if (!await store.ReleaseAsync(key.Id, request.InvocationId).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "CK method {MethodId} invocation {InvocationId}: the idempotency record {KeyId} could not be released (no longer owned or not in progress)",
                    request.MethodId, request.InvocationId, key.Id);
            }
        }
        catch (Exception e)
        {
            // not released: the key stays in progress until the lease lapses (then burned), never reusable by guess
            logger.LogError(e,
                "CK method {MethodId} invocation {InvocationId}: the idempotency claim {KeyId} could not be released; it stays in progress until its lease lapses",
                request.MethodId, request.InvocationId, key.Id);
        }
    }

    private CkMethodResponse StoreUnavailable(CkMethodRequest request, CkMethodIdempotencyKey key, Exception e)
    {
        logger.LogError(e,
            "CK method {MethodId} invocation {InvocationId} not executed: the idempotency store could not decide for key {KeyId} (fail closed)",
            request.MethodId, request.InvocationId, key.Id);
        return CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerNotAvailable,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.Reason, CkMethodIdempotencyRefusal.StoreUnavailable)));
    }

    private static CkMethodResponse Refuse(CkMethodRequest request, string reason) =>
        CkMethodResponse.Failed(request.InvocationId, string.Empty,
            CkMethodError.Create(CkMethodErrorCodes.MethodValidationFailed,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.Field, CkMethodIdempotencyRefusal.Field),
                (CkMethodErrorParameters.Reason, reason)));
}
