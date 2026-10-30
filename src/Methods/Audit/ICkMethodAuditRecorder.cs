using System.Diagnostics.Metrics;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Audit;

/// <summary>
///     Writes the audit event of one CK method invocation (AB#6919): category <c>Method.Invocation</c>, metadata from
///     <see cref="CkMethodAuditRecord" />. The band-100 audit filter calls it for every call that enters the pipeline; the
///     gateway (AB#7826) calls it for the calls it refuses before the pipeline, so the event repository holds exactly one event
///     per invocation id whichever way the call ended. Register with <c>AddCkMethodAudit()</c>.
/// </summary>
public interface ICkMethodAuditRecorder
{
    /// <summary>
    ///     Writes the event. A failed write is logged and counted and does <b>not</b> throw, unless the tenant is configured to
    ///     fail the call on an audit failure (<see cref="CkMethodAuditOptions" />): then it throws
    ///     <see cref="CkMethodAuditFailedException" /> and the caller must answer the call as failed. Caller cancellation
    ///     (<paramref name="cancellationToken" />) is not an audit failure and is rethrown.
    /// </summary>
    /// <param name="record">What to record; it cannot carry parameters, results or tokens.</param>
    /// <param name="cancellationToken">Cancellation of the write. Pass <c>CancellationToken.None</c> to record a cancelled call.</param>
    /// <exception cref="CkMethodAuditFailedException">The write failed and the tenant fails calls on audit failure.</exception>
    Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>The audit event could not be written and the configuration says the call must fail then.</summary>
public sealed class CkMethodAuditFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="invocationId">The invocation whose event is missing.</param>
    /// <param name="inner">The failure of the store.</param>
    public CkMethodAuditFailedException(Guid invocationId, Exception inner)
        : base($"The audit event of CK method invocation {invocationId} could not be written.", inner)
    {
        InvocationId = invocationId;
    }

    /// <summary>The invocation whose event is missing.</summary>
    public Guid InvocationId { get; }
}

internal sealed class CkMethodAuditRecorder : ICkMethodAuditRecorder
{
    /// <summary>Name of the meter.</summary>
    internal const string MeterName = CkMethodTelemetry.MeterName;

    internal const string FailureCounterName = "ck.method.audit.failures";

    private static readonly Counter<long> Failures = CkMethodTelemetry.Meter.CreateCounter<long>(FailureCounterName,
        description: "Audit events of CK method invocations that could not be written");

    private readonly IAuditEventSink _sink;
    private readonly IOptionsMonitor<CkMethodAuditOptions> _options;
    private readonly ILogger<CkMethodAuditRecorder> _logger;

    public CkMethodAuditRecorder(IAuditEventSink sink, IOptionsMonitor<CkMethodAuditOptions> options,
        ILogger<CkMethodAuditRecorder> logger)
    {
        _sink = sink;
        _options = options;
        _logger = logger;
    }

    private int _lenientSinkWarned;

    private void WarnOnceAboutLenientSink(string tenantId)
    {
        if (_options.CurrentValue.FailsCallFor(tenantId) && Interlocked.Exchange(ref _lenientSinkWarned, 1) == 0)
        {
            _logger.LogWarning(
                "Audit sink {SinkType} does not implement IStrictAuditEventSink: audit write failures cannot be counted and cannot fail the call (first seen for tenant {TenantId})",
                _sink.GetType().Name, tenantId);
        }
    }

    public async Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // A call the gateway refused never enters the pipeline, so this is its only place to be counted (AB#6920); the
        // calls of the audit filter are counted by the pipeline itself, exactly once each.
        if (string.Equals(record.Transport, CkMethodAuditRecord.GatewayTransport, StringComparison.Ordinal))
        {
            CkMethodMetrics.RecordCall(record.MethodId, record.Transport, record.Outcome, record.Duration);
        }

        try
        {
            var auditEvent = record.ToAuditEvent();
            // the strict write reports a failure; the plain contract swallows it, then nothing could be counted
            Task write;
            if (_sink is IStrictAuditEventSink strict)
            {
                write = strict.PublishStrictAsync(auditEvent, cancellationToken);
            }
            else
            {
                WarnOnceAboutLenientSink(record.TenantId);
                write = _sink.PublishAsync(auditEvent, cancellationToken);
            }

            // bounded: the event repository takes no token, a stalled store must not keep the call pending
            var timeout = _options.CurrentValue.WriteTimeout;
            if (timeout > TimeSpan.Zero)
            {
                await write.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await write.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("transport", record.Transport));
            // ids and outcome only; the exception of the store is logged as is (it carries no request data)
            _logger.LogError(e,
                "Audit event of CK method {MethodId} invocation {InvocationId} (tenant {TenantId}, outcome {Outcome}) could not be written",
                record.MethodId, record.InvocationId, record.TenantId, record.Outcome);
            if (_options.CurrentValue.FailsCallFor(record.TenantId))
            {
                throw new CkMethodAuditFailedException(record.InvocationId, e);
            }
        }
    }
}
