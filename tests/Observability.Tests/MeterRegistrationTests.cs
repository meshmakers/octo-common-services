using Meshmakers.Octo.Runtime.Engine.Secrets;
using Meshmakers.Octo.Services.Observability;

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
}
