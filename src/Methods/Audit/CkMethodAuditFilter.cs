using System.Diagnostics;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods.Audit;

/// <summary>
///     The outermost filter of the pipeline (band <see cref="CkMethodInvocationFilterOrder.Audit" />, AB#6919): writes
///     exactly one audit event per invocation through <see cref="ICkMethodAuditRecorder" />, with the final outcome - success,
///     a declared business error, a platform error code, a refusal by authorization (band 200), an answer of the idempotency
///     filter (band 300, marked with <c>replayOf</c> from <see cref="CkMethodInvocationFacts.ReplayOf" />), a timeout.
/// </summary>
/// <remarks>
///     It sees only the redacted view of the request and reads nothing of the parameters, the result, the token, the
///     idempotency key or the fingerprint: the record has no place for them. An exception from an inner filter is recorded
///     as <c>METHOD_HANDLER_FAILED</c> and rethrown (the pipeline answers as before); a cancellation by the caller is recorded
///     as <see cref="CkMethodAuditOutcomes.CallerCancelled" /> and rethrown. The caller is the trusted
///     <see cref="ICkMethodPrincipalProvider" /> principal, falling back to <c>request.Caller</c>.
/// </remarks>
public sealed class CkMethodAuditFilter(
    ICkMethodAuditRecorder recorder,
    ICkMethodPrincipalProvider principalProvider,
    ILogger<CkMethodAuditFilter> logger,
    TimeProvider? timeProvider = null) : ICkMethodInvocationFilter
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public int Order => CkMethodInvocationFilterOrder.Audit;

    /// <inheritdoc />
    public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Request;
        var started = _time.GetTimestamp();
        var subject = SafeSubject(context);
        CkMethodResponse response;
        try
        {
            response = await next(request).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            await RecordOrFailAsync(context, subject, started, CkMethodAuditOutcomes.CallerCancelled, null)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RecordOrFailAsync(context, subject, started, CkMethodErrorCodes.MethodHandlerFailed, null)
                .ConfigureAwait(false);
            throw;
        }

        var replacement = await RecordOrFailAsync(context, subject, started, CkMethodAuditRecord.OutcomeOf(response),
            response).ConfigureAwait(false);
        return replacement ?? response;
    }

    private async Task<CkMethodResponse?> RecordOrFailAsync(CkMethodInvocationContext context, string? subject,
        long started, string outcome, CkMethodResponse? response)
    {
        var request = context.Request;
        var record = CkMethodAuditRecord.For(request, outcome, context.Transport, _time.GetElapsedTime(started), subject,
            context.Facts.ReplayOf, response?.HandlerInstanceId);
        try
        {
            // the caller may have given up; the event is written anyway
            await recorder.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (CkMethodAuditFailedException) when (response != null)
        {
            return AuditFailureResponse(request, response);
        }
        catch (CkMethodAuditFailedException)
        {
            // the call already ended with an exception that propagates; the failure is logged by the recorder
            return null;
        }
        catch (Exception e)
        {
            // a custom recorder must not fail the call by accident
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit recorder threw",
                request.MethodId, request.InvocationId);
            return null;
        }
    }

    /// <summary>
    ///     A call that was not executed stays the refusal it was; every other call may have taken effect, so the answer is
    ///     <c>METHOD_OUTCOME_UNKNOWN</c>.
    /// </summary>
    private static CkMethodResponse AuditFailureResponse(CkMethodRequest request, CkMethodResponse response)
    {
        if (!response.Success && response.Error is { } error &&
            CkMethodErrorCodes.Catalog.Any(c => c.Code == error.Code && c.Execution == CkMethodExecutionState.NotExecuted))
        {
            return response;
        }

        return CkMethodResponse.Failed(request.InvocationId, response.HandlerInstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                (CkMethodErrorParameters.MethodId, request.MethodId)));
    }

    private string? SafeSubject(CkMethodInvocationContext context)
    {
        try
        {
            return principalProvider.GetPrincipal(context)?.Subject;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "CK method {MethodId}: the caller for the audit event could not be determined",
                context.Request.MethodId);
            return null;
        }
    }
}
