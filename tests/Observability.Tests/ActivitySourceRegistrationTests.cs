using System.Diagnostics;

using Meshmakers.Octo.Services.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Trace;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     Pins that <c>AddObservability()</c> subscribes the tracer provider to the ActivitySources
///     whose spans the charts export via OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES.
/// </summary>
/// <remarks>
///     An ActivitySource nobody listens to never creates an Activity, so a missing subscription
///     has no symptom except missing spans. <see cref="ActivitySource.HasListeners" /> turns true
///     once the tracer provider subscribed to the source name, so it checks the registration
///     directly.
/// </remarks>
public class ActivitySourceRegistrationTests
{
    /// <summary>
    ///     AB#6307: MongoDB.Driver 3.x command spans exist, so a slow request shows its database
    ///     calls instead of a server span without children.
    /// </summary>
    [Fact]
    public void MongoDbDriverSource_IsRegistered()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddObservability();

        using var host = builder.Build();
        _ = host.Services.GetRequiredService<TracerProvider>();

        using var source = new ActivitySource("MongoDB.Driver");
        Assert.True(source.HasListeners());
    }
}
