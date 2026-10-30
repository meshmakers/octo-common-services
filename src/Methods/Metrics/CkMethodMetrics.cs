using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;

namespace Meshmakers.Octo.Services.Methods.Metrics;

/// <summary>Values of the <c>reason</c> label of <c>ck.method.duplicate_refused</c> (AB#6920, OD-3).</summary>
public static class CkMethodDuplicateReasons
{
    /// <summary>The broker flagged the delivery as a possible duplicate (redelivery, bus retry).</summary>
    public const string BrokerFlag = "broker_flag";

    /// <summary>The broker did not flag it, but this instance has seen the invocation id before.</summary>
    public const string SeenId = "seen_id";
}

/// <summary>
///     The instruments of the meter <see cref="CkMethodTelemetry.MeterName" /> that describe method calls (AB#6920). The
///     meter is the one the audit recorder already uses (<c>ck.method.audit.failures</c>, AB#6919).
/// </summary>
/// <remarks>
///     <b>Labels, and what they never contain.</b> <c>method_id</c> (qualified method id, model version-less),
///     <c>transport</c> (<c>bus</c>, <c>in-process</c>, <c>gateway</c>), <c>outcome</c> (<c>SUCCESS</c>, <c>ACCEPTED</c>,
///     <c>CALLER_CANCELLED</c> or an error code, same vocabulary as the audit event) and <c>reason</c> (duplicates only). No
///     parameter, result, token, entity id, user or tenant: the existing Methods meter carries no tenant either, and the
///     tenant is not needed to see which method fails. The label values are bounded: an unknown transport is
///     <c>other</c>, an error code that is neither a platform code nor a well-formed declared code is <c>invalid_code</c>,
///     and after <see cref="MaxMethodIds" /> distinct method ids further ids are counted as <c>other</c>, so a caller that
///     forges ids cannot grow the series without limit.
/// </remarks>
internal static class CkMethodMetrics
{
    internal const string CallsName = "ck.method.calls";
    internal const string DurationName = "ck.method.duration";
    internal const string NoHandlerName = "ck.method.no_handler";
    internal const string DuplicateRefusedName = "ck.method.duplicate_refused";

    internal const string MethodIdTag = "method_id";
    internal const string TransportTag = "transport";
    internal const string OutcomeTag = "outcome";
    internal const string ReasonTag = "reason";

    internal const string Other = "other";
    internal const string InvalidCode = "invalid_code";

    /// <summary>Upper bound of distinct <c>method_id</c> label values per process.</summary>
    internal const int MaxMethodIds = 1000;

    private static readonly Counter<long> Calls = CkMethodTelemetry.Meter.CreateCounter<long>(CallsName,
        description: "CK method calls by method, transport and outcome (every call counted once: pipeline for bus and in-process, recorder for calls the gateway refused)");

    private static readonly Histogram<double> Duration = CkMethodTelemetry.Meter.CreateHistogram<double>(DurationName,
        "s", "Duration of CK method calls as the caller sees it, by method, transport and outcome",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300]
        });

    private static readonly Counter<long> NoHandler = CkMethodTelemetry.Meter.CreateCounter<long>(NoHandlerName,
        description: "CK method calls that found no handler (METHOD_HANDLER_NOT_AVAILABLE), by method and transport");

    private static readonly Counter<long> DuplicateRefused = CkMethodTelemetry.Meter.CreateCounter<long>(
        DuplicateRefusedName,
        description: "Deliveries a handler host refused as possible duplicates (not executed), by reason (broker_flag, seen_id) and method");

    private static readonly CkMethodLabelBudget MethodIds = new(MaxMethodIds);

    /// <summary>Counts one finished call and records its duration.</summary>
    internal static void RecordCall(string methodId, string transport, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { MethodIdTag, MethodIdLabel(methodId) },
            { TransportTag, TransportLabel(transport) },
            { OutcomeTag, OutcomeLabel(outcome) },
        };
        Calls.Add(1, tags);
        Duration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>Counts a call that found no handler.</summary>
    internal static void RecordNoHandler(string methodId, string transport)
    {
        NoHandler.Add(1, new TagList
        {
            { MethodIdTag, MethodIdLabel(methodId) },
            { TransportTag, TransportLabel(transport) },
        });
    }

    /// <summary>Counts a delivery refused as a possible duplicate.</summary>
    internal static void RecordDuplicateRefused(string methodId, string reason)
    {
        DuplicateRefused.Add(1, new TagList
        {
            { ReasonTag, reason },
            { MethodIdTag, MethodIdLabel(methodId) },
        });
    }

    /// <summary>
    ///     The outcome label of a finished call: the audit vocabulary, and whether the call found no handler. A refusal
    ///     because the idempotency store was down uses the same code but is not a missing handler.
    /// </summary>
    internal static void RecordFinished(string methodId, string transport, string outcome, TimeSpan duration,
        CkMethodResponse? response)
    {
        RecordCall(methodId, transport, outcome, duration);
        if (response is { Success: false, Error: { } error } &&
            string.Equals(error.Code, CkMethodErrorCodes.MethodHandlerNotAvailable, StringComparison.Ordinal) &&
            !(error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason) &&
              string.Equals(reason, CkMethodIdempotencyRefusal.StoreUnavailable, StringComparison.Ordinal)))
        {
            RecordNoHandler(methodId, transport);
        }
    }

    internal static string MethodIdLabel(string? methodId) => MethodIds.Label(methodId);

    private static string TransportLabel(string transport) => transport switch
    {
        CkMethodTransportNames.Bus or CkMethodTransportNames.InProcess or CkMethodAuditRecord.GatewayTransport => transport,
        _ => Other,
    };

    private static string OutcomeLabel(string outcome) =>
        outcome is CkMethodAuditOutcomes.Success or CkMethodAuditOutcomes.Accepted or CkMethodAuditOutcomes.CallerCancelled
        || CkMethodErrorCodes.All.Contains(outcome) || CkMethodErrorCodes.IsValidDeclaredCode(outcome)
            ? outcome
            : InvalidCode;
}

/// <summary>
///     Bounds the distinct values of a label: the first <c>max</c> values pass, every further one is reported as
///     <c>other</c>. A value that was admitted stays admitted.
/// </summary>
internal sealed class CkMethodLabelBudget(int max)
{
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);

    /// <summary>The label value to report for <paramref name="value" />.</summary>
    public string Label(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return CkMethodMetrics.Other;
        }

        if (_known.ContainsKey(value))
        {
            return value;
        }

        // not atomic against a burst; the bound holds up to the number of racing threads, which is fine for a safety valve
        if (_known.Count >= max)
        {
            return CkMethodMetrics.Other;
        }

        _known.TryAdd(value, 0);
        return value;
    }
}
