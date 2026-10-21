using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Metrics of tenants whose setup or deferred start is failing (AB#7249). Both series carry the tenant,
///     exist only while the tenant is failing and disappear when it recovers, so an alert rule is a plain
///     threshold on <c>octo.tenant_setup.pending_seconds</c> (or "series exists" on
///     <c>octo.tenant_setup.failed</c>).
/// </summary>
/// <remarks>
///     Meter <see cref="MeterName" /> is registered in <c>ObservabilityBuilder</c> by this string: an
///     unregistered meter drops every measurement silently (AB#5430).
/// </remarks>
public sealed class TenantSetupMetrics : IDisposable
{
    /// <summary>Name of the meter.</summary>
    public const string MeterName = "Meshmakers.Octo.TenantSetup";

    /// <summary>Phase tag value: <c>SetupAsync</c> failed.</summary>
    public const string PhaseSetup = "setup";

    /// <summary>Phase tag value: the deferred tenant start failed.</summary>
    public const string PhaseStart = "start";

    private readonly Meter _meter;

    /// <summary>Creates the instruments over the tracker and the registry.</summary>
    public TenantSetupMetrics(TenantSetupRetryTracker tracker, FailedTenantRegistry registry,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        _meter = new Meter(MeterName);

        _meter.CreateObservableGauge(
            "octo.tenant_setup.failed",
            () => Observe(tracker, registry, time, (_, _) => 1.0),
            unit: "{tenant}",
            description: "1 for every tenant whose setup or start is currently failing and being retried");

        _meter.CreateObservableGauge(
            "octo.tenant_setup.pending_seconds",
            () => Observe(tracker, registry, time, (first, now) => (now - first).TotalSeconds),
            unit: "s",
            description: "Seconds since the tenant's setup or start first failed; alert when it exceeds the " +
                         "tolerated outage (default degraded threshold: 10 minutes)");
    }

    internal static IEnumerable<Measurement<double>> Observe(TenantSetupRetryTracker tracker,
        FailedTenantRegistry registry, TimeProvider time, Func<DateTimeOffset, DateTimeOffset, double> value)
    {
        var now = time.GetUtcNow();
        foreach (var e in tracker.Snapshot())
        {
            yield return new Measurement<double>(value(e.FirstFailedAt, now),
                new KeyValuePair<string, object?>("octo.tenant.id", e.TenantId),
                new KeyValuePair<string, object?>("octo.tenant_setup.phase", PhaseSetup));
        }

        foreach (var e in registry.Snapshot())
        {
            yield return new Measurement<double>(value(e.FirstFailedAt, now),
                new KeyValuePair<string, object?>("octo.tenant.id", e.TenantId),
                new KeyValuePair<string, object?>("octo.tenant_setup.phase", PhaseStart));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
