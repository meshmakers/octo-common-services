using Meshmakers.Octo.Runtime.Engine.AuditTrails;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.RestDeprecation;

using Microsoft.AspNetCore.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Metrics;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     Pins that <c>AddObservability()</c> subscribes the meter provider to the engine meters.
/// </summary>
/// <remarks>
///     An unregistered meter has no symptom: instruments are created, measurements are taken and
///     every one of them is dropped (AB#5430). <see cref="System.Diagnostics.Metrics.Instrument.Enabled" />
///     only turns true once a listener — here the OpenTelemetry meter provider — subscribed to the
///     instrument's meter, so it is a direct check of the registration.
/// </remarks>
public class MeterRegistrationTests
{
    /// <summary>
    ///     AB#5531: octo.secrets.decrypt / octo.secrets.plaintext_reads (SECRET attribute value type,
    ///     AB#5528) reach the exporters.
    /// </summary>
    [Fact]
    public void SecretsMeter_IsRegistered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();

        Assert.True(SecretDiagnostics.Decrypts.Enabled);
        Assert.True(SecretDiagnostics.PlaintextReads.Enabled);
    }

    /// <summary>
    ///     AB#7279: octo.datapermissions.audit.violations (DataPermissionAudit in the engine) reaches the exporters, so
    ///     the full count of AuditOnly violations stays queryable while the audit log only carries window summaries.
    /// </summary>
    [Fact]
    public void DataPermissionAuditMeter_IsRegistered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();

        Assert.True(DataPermissionAudit.Violations.Enabled);
    }

    /// <summary>
    ///     AB#7249: octo.tenant_setup.failed / .pending_seconds (TenantSetupMetrics in Infrastructure, meter name
    ///     pinned there by a test) reach the exporters. Observability does not reference Infrastructure, so the
    ///     meter is created here under the same name.
    /// </summary>
    [Fact]
    public void TenantSetupMeter_IsRegistered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();

        using var meter = new System.Diagnostics.Metrics.Meter("Meshmakers.Octo.TenantSetup");
        var counter = meter.CreateCounter<int>("octo.tenant_setup.probe");

        Assert.True(counter.Enabled);
    }

    /// <summary>
    ///     AB#6920 / AB#6919: ck.method.calls, .duration, .no_handler, .duplicate_refused and ck.method.audit.failures
    ///     (meter of the Methods package, name pinned there by a test) reach the exporters.
    /// </summary>
    [Fact]
    public void MethodsMeter_IsRegistered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();

        using var meter = new System.Diagnostics.Metrics.Meter("Meshmakers.Octo.Methods");
        var counter = meter.CreateCounter<int>("ck.method.probe");

        Assert.True(counter.Enabled);
    }
    /// <summary>
    ///     AB#8421: octo.rest.deprecated.calls (RestDeprecationMetrics) reaches the exporters, and AddObservability() installs
    ///     the deprecation startup filter, so a service only puts [OctoRestDeprecated] on an action.
    /// </summary>
    [Fact]
    public void RestDeprecationMeter_IsRegistered_AndStartupFilterInstalled()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<MeterProvider>();

        using var meter = new System.Diagnostics.Metrics.Meter(RestDeprecationMetrics.MeterName);
        var counter = meter.CreateCounter<int>("octo.rest.deprecated.probe");

        Assert.True(counter.Enabled);
        Assert.Contains(host.Services.GetServices<IStartupFilter>(),
            f => f.GetType().FullName == "Meshmakers.Octo.Services.RestDeprecation.RestDeprecationStartupFilter");
    }
}
