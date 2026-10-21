using Meshmakers.Octo.Services.Infrastructure.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>AB#7249 — the DI registration of the tenant setup retry (queue, options, metrics, health check).</summary>
public class TenantSetupRetryRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
        services.AddTenantSetupRetry();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheContainerHandsOutTheProcessWideQueue_SoServicesWithoutWiringShareIt()
    {
        using var sp = Build();

        Assert.Same(TenantSetupRetryTracker.Default, sp.GetRequiredService<TenantSetupRetryTracker>());
    }

    [Fact]
    public void OptionsAreBoundFromConfiguration_AndReachTheQueue()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["TenantSetupRetry:InitialDelay"] = "00:00:10",
            ["TenantSetupRetry:MaxDelay"] = "00:02:00",
            ["TenantSetupRetry:DegradedAfter"] = "00:03:00"
        });

        var tracker = sp.GetRequiredService<TenantSetupRetryTracker>();

        try
        {
            Assert.Equal(TimeSpan.FromSeconds(10), tracker.Options.InitialDelay);
            Assert.Equal(TimeSpan.FromMinutes(2), tracker.Options.MaxDelay);
            Assert.Equal(TimeSpan.FromMinutes(3), tracker.Options.DegradedAfter);
        }
        finally
        {
            tracker.Configure(new TenantSetupRetryOptions()); // Default is process-wide: do not leak into other tests
        }
    }

    [Fact]
    public async Task TheHealthCheckIsRegistered_ButNotTaggedReady()
    {
        using var sp = Build();

        var registration = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(r => r.Name == "TenantSetup");
        Assert.DoesNotContain("ready", registration.Tags);

        var report = await sp.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "TenantSetup", TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public void MetricsAndRegistryResolve()
    {
        using var sp = Build();

        Assert.NotNull(sp.GetRequiredService<TenantSetupMetrics>());
        Assert.NotNull(sp.GetRequiredService<FailedTenantRegistry>());
    }
}
