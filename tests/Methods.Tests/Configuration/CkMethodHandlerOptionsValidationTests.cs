using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Configuration;

/// <summary>Final, validated heartbeat and TTL values of a handler host (AB#7838).</summary>
public class CkMethodHandlerOptionsValidationTests
{
    private static ValidateOptionsResult Validate(TimeSpan interval, TimeSpan ttl) =>
        new CkMethodHandlerOptionsValidator().Validate(null,
            new CkMethodHandlerOptions { HeartbeatInterval = interval, Ttl = ttl });

    [Fact]
    public void Defaults_AreFifteenAndFortyFiveSeconds_AndValid()
    {
        var options = new CkMethodHandlerOptions();

        Assert.Equal(TimeSpan.FromSeconds(15), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Ttl);
        Assert.True(Validate(options.HeartbeatInterval, options.Ttl).Succeeded);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(1, 2)]
    [InlineData(15, 30)] // exactly twice the interval
    [InlineData(60, 86400)] // exactly the registry clamp
    public void ValidOverrides_Succeed(int intervalSeconds, int ttlSeconds) =>
        Assert.True(Validate(TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(ttlSeconds)).Succeeded);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void HeartbeatIntervalBelowOneSecond_FailsNamingKeyAndRule(int intervalSeconds)
    {
        var result = Validate(TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(45));

        Assert.True(result.Failed);
        var message = Assert.Single(result.Failures!);
        Assert.Contains("Methods:HeartbeatInterval", message);
        Assert.Contains("at least 00:00:01", message);
    }

    [Fact]
    public void SubSecondHeartbeatInterval_Fails() =>
        Assert.True(Validate(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(45)).Failed);

    [Fact]
    public void TtlBelowTwiceTheInterval_FailsNamingKeyAndRule()
    {
        var result = Validate(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(29));

        var message = Assert.Single(result.Failures!);
        Assert.Contains("Methods:Ttl", message);
        Assert.Contains("2 x Methods:HeartbeatInterval", message);
        Assert.Contains("00:00:30", message);
    }

    [Fact]
    public void TtlAboveTwentyFourHours_FailsNamingKeyAndRule()
    {
        var result = Validate(TimeSpan.FromSeconds(15), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        var message = Assert.Single(result.Failures!);
        Assert.Contains("Methods:Ttl", message);
        Assert.Contains("at most 1.00:00:00", message);
    }

    [Fact]
    public void ZeroTtl_IsReportedOnce_NotAsAMultipleOfAnInvalidInterval()
    {
        var result = Validate(TimeSpan.Zero, TimeSpan.Zero);

        // the interval failure only: the TTL ratio is not judged against an unusable interval
        Assert.Single(result.Failures!);
    }

    [Fact]
    public void SeveralViolations_AreAllReported()
    {
        var result = Validate(TimeSpan.Zero, TimeSpan.FromDays(2));

        Assert.Equal(2, result.Failures!.Count());
    }

    private static IHost HostWith(Dictionary<string, string?> configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddSingleton(A.Fake<IDistributionEventHubService>()); // resolved with the hosted services
        builder.Services.AddCkMethodHandlers("identity-services", m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        return builder.Build();
    }

    [Theory]
    [InlineData("0", "45")]
    [InlineData("15", "20")]
    [InlineData("15", "2.00:00:00")]
    public async Task InvalidConfiguration_StopsTheHostAtStartup_WithTheKeyInTheMessage(string interval, string ttl)
    {
        using var host = HostWith(new()
        {
            ["Methods:HeartbeatInterval"] = interval.Contains(':') ? interval : $"00:00:{int.Parse(interval):00}",
            ["Methods:Ttl"] = ttl.Contains(':') ? ttl : $"00:00:{int.Parse(ttl):00}"
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(exception.Failures, f => f.Contains("Methods:"));
    }

    [Fact]
    public void ValidOverride_ResolvesTheOptions_WithoutAFailure()
    {
        using var host = HostWith(new()
        {
            ["Methods:HeartbeatInterval"] = "00:00:10",
            ["Methods:Ttl"] = "00:00:30"
        });

        var options = host.Services.GetRequiredService<IOptions<CkMethodHandlerOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(10), options.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Ttl);
    }

    [Theory]
    [InlineData(15, 45)]
    [InlineData(10, 30)]
    public void Announcement_CarriesTheConfiguredValues_AndTheRegistryExpiresAtReceiptPlusTtl(int interval, int ttl)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        registrations.Add(TestData.MethodId, typeof(ChangePasswordHandler));
        var instance = new CkMethodHandlerInstance(registrations, Options.Create(new CkMethodHandlerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(interval),
            Ttl = TimeSpan.FromSeconds(ttl)
        }), time);

        var announcement = instance.CreateAnnouncement();
        Assert.Equal(interval, announcement.HeartbeatIntervalSeconds);
        Assert.Equal(ttl, announcement.TtlSeconds);

        var registry = new CkMethodHandlerRegistry(time);
        registry.OnAnnounced(announcement);
        var entry = Assert.Single(registry.GetLiveHandlers(TestData.MethodId));
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddSeconds(ttl), entry.ExpiresUtc);

        time.Advance(TimeSpan.FromSeconds(ttl - 1));
        Assert.Single(registry.GetLiveHandlers(TestData.MethodId));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(registry.GetLiveHandlers(TestData.MethodId));
    }
}
