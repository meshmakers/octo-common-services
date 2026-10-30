using System.Globalization;
using System.Text;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Audit;

/// <summary>Constants of the audit event of a CK method invocation.</summary>
public static class CkMethodAuditEvent
{
    /// <summary><c>AuditEvent.Category</c> of every method invocation event; the auditor's filter.</summary>
    public const string Category = "Method.Invocation";

    /// <summary>Keys of <c>AuditEvent.Metadata</c> (persisted as the details of the event).</summary>
    public static class Keys
    {
        /// <summary>Id of the invocation.</summary>
        public const string InvocationId = "invocationId";

        /// <summary>Qualified method id.</summary>
        public const string MethodId = "methodId";

        /// <summary>CK type that declares the method.</summary>
        public const string CkTypeId = "ckTypeId";

        /// <summary>Target entity of an instance method; absent for a static method.</summary>
        public const string TargetRtId = "targetRtId";

        /// <summary>Subject (or client id of a service token) of the caller.</summary>
        public const string CallerSubject = "callerSubject";

        /// <summary>Display name of the caller, when the caller sent one.</summary>
        public const string CallerName = "callerName";

        /// <summary><c>in-process</c> or <c>bus</c>, or <c>gateway</c> for a refusal before the pipeline.</summary>
        public const string Transport = "transport";

        /// <summary>Instance that ran the handler; absent when none did.</summary>
        public const string HandlerInstanceId = "handlerInstanceId";

        /// <summary>Final outcome, see <see cref="CkMethodAuditOutcomes" />.</summary>
        public const string Outcome = "outcome";

        /// <summary>Duration of the call in milliseconds.</summary>
        public const string DurationMs = "durationMs";

        /// <summary>Id of the first invocation when the call was answered from the idempotency store; absent otherwise.</summary>
        public const string ReplayOf = "replayOf";
    }
}

/// <summary>Outcome values of the audit event beside the error codes of <see cref="CkMethodErrorCodes" />.</summary>
public static class CkMethodAuditOutcomes
{
    /// <summary>The method ran and succeeded.</summary>
    public const string Success = "SUCCESS";

    /// <summary>The method was accepted as a long-running operation.</summary>
    public const string Accepted = "ACCEPTED";

    /// <summary>The caller gave up (cancelled) before an answer; the method may or may not have run.</summary>
    public const string CallerCancelled = "CALLER_CANCELLED";
}

/// <summary>
///     The facts of one method invocation that the audit keeps (AB#6919). Deliberately no parameter, no result, no token,
///     no idempotency key and no fingerprint: the record cannot carry them. Built by the audit filter from the redacted
///     view of the pipeline and by the gateway (AB#7826) for a call it refuses before the pipeline.
/// </summary>
public sealed record CkMethodAuditRecord
{
    private const int MaxTextLength = 256;

    /// <summary>The tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The invocation id; every attempt has its own, also a replay.</summary>
    public required Guid InvocationId { get; init; }

    /// <summary>Qualified method id.</summary>
    public required string MethodId { get; init; }

    /// <summary>CK type id that declares the method; empty when the caller named none.</summary>
    public string CkTypeId { get; init; } = string.Empty;

    /// <summary>Target entity of an instance method.</summary>
    public string? TargetRtId { get; init; }

    /// <summary><see cref="CkMethodTransportNames" /> or <see cref="GatewayTransport" />.</summary>
    public required string Transport { get; init; }

    /// <summary><see cref="CkMethodAuditOutcomes" /> or the error code (platform or declared) of the final answer.</summary>
    public required string Outcome { get; init; }

    /// <summary>Transport name for a refusal by the gateway before the pipeline.</summary>
    public const string GatewayTransport = "gateway";

    /// <summary>Subject or client id of the caller.</summary>
    public string? CallerSubject { get; init; }

    /// <summary>Display name of the caller.</summary>
    public string? CallerName { get; init; }

    /// <summary>The handler instance that answered.</summary>
    public string? HandlerInstanceId { get; init; }

    /// <summary>How long the call took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>First invocation id when this call is a replay.</summary>
    public Guid? ReplayOf { get; init; }

    /// <summary>The outcome of a response: success, accepted, or the error code. Never the error parameters or the result.</summary>
    public static string OutcomeOf(CkMethodResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Success)
        {
            return response.Error?.Code is { Length: > 0 } code ? code : CkMethodErrorCodes.MethodHandlerFailed;
        }

        return response.IsAccepted ? CkMethodAuditOutcomes.Accepted : CkMethodAuditOutcomes.Success;
    }

    /// <summary>Builds the record of an answered call.</summary>
    /// <param name="request">The request (the redacted view is fine; nothing of the parameters is read).</param>
    /// <param name="response">The final answer.</param>
    /// <param name="transport">Transport name.</param>
    /// <param name="duration">Duration.</param>
    /// <param name="callerSubject">The trusted caller; falls back to <c>request.Caller</c>.</param>
    /// <param name="replayOf">First invocation id when the call was a replay.</param>
    public static CkMethodAuditRecord FromResponse(CkMethodRequest request, CkMethodResponse response, string transport,
        TimeSpan duration, string? callerSubject = null, Guid? replayOf = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return For(request, OutcomeOf(response), transport, duration, callerSubject, replayOf,
            response.HandlerInstanceId);
    }

    /// <summary>Builds the record of a call with a given outcome (an exception, a cancellation, a refusal).</summary>
    public static CkMethodAuditRecord For(CkMethodRequest request, string outcome, string transport, TimeSpan duration,
        string? callerSubject = null, Guid? replayOf = null, string? handlerInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CkMethodAuditRecord
        {
            TenantId = request.TenantId,
            InvocationId = request.InvocationId,
            MethodId = request.MethodId,
            CkTypeId = request.CkTypeId,
            TargetRtId = request.TargetRtId,
            Transport = transport,
            Outcome = outcome,
            CallerSubject = callerSubject ?? request.Caller?.SubjectId ?? request.Caller?.ClientId,
            CallerName = request.Caller?.Name,
            HandlerInstanceId = handlerInstanceId,
            Duration = duration,
            ReplayOf = replayOf,
        };
    }

    internal AuditEvent ToAuditEvent()
    {
        var metadata = new Dictionary<string, object?>
        {
            [CkMethodAuditEvent.Keys.InvocationId] = InvocationId.ToString(),
            [CkMethodAuditEvent.Keys.MethodId] = Clean(MethodId),
            [CkMethodAuditEvent.Keys.CkTypeId] = Clean(CkTypeId),
            [CkMethodAuditEvent.Keys.TargetRtId] = Clean(TargetRtId),
            [CkMethodAuditEvent.Keys.CallerSubject] = Clean(CallerSubject),
            [CkMethodAuditEvent.Keys.CallerName] = Clean(CallerName),
            [CkMethodAuditEvent.Keys.Transport] = Clean(Transport),
            [CkMethodAuditEvent.Keys.HandlerInstanceId] = Clean(HandlerInstanceId),
            [CkMethodAuditEvent.Keys.Outcome] = Clean(Outcome),
            [CkMethodAuditEvent.Keys.DurationMs] = ((long)Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            [CkMethodAuditEvent.Keys.ReplayOf] = ReplayOf?.ToString(),
        };
        var message = string.Create(CultureInfo.InvariantCulture,
            $"CK method {Clean(MethodId)} invoked by {Clean(CallerSubject) ?? "unknown caller"} " +
            $"via {Clean(Transport)}: {Clean(Outcome)}{(ReplayOf is null ? string.Empty : " (replay)")} in {(long)Duration.TotalMilliseconds} ms");
        return new AuditEvent(TenantId, LevelOf(Outcome), CkMethodAuditEvent.Category, message) { Metadata = metadata };
    }

    private static AuditEventLevel LevelOf(string outcome) => outcome switch
    {
        CkMethodAuditOutcomes.Success or CkMethodAuditOutcomes.Accepted => AuditEventLevel.Information,
        CkMethodErrorCodes.MethodHandlerFailed or CkMethodErrorCodes.MethodTimeout
            or CkMethodErrorCodes.MethodOutcomeUnknown => AuditEventLevel.Error,
        // refusals, replays of a refusal, declared business errors, cancellation: not the platform's failure
        _ => AuditEventLevel.Warning,
    };

    /// <summary>Bounds the text and drops control characters (no forged lines in the readable message).</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        foreach (var c in text)
        {
            if (builder.Length >= MaxTextLength)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}
