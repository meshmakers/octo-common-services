using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#8177 — a tenant whose MongoDB user is gone failed its setup retry with the same authentication error
///     forever (617 attempts in 7 days). The retry loop now re-ensures the tenant's database user before the retry,
///     so one retry cycle heals the tenant.
/// </summary>
public class FailedTenantRetryLoopDatabaseUserTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly TenantSetupRetryTracker _tracker;
    private readonly FailedTenantRegistry _registry;
    private readonly IDefaultConfigurationCreatorService _creator = A.Fake<IDefaultConfigurationCreatorService>();
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly List<string> _calls = [];

    public FailedTenantRetryLoopDatabaseUserTests()
    {
        _tracker = new TenantSetupRetryTracker(_time);
        _registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
    }

    private FailedTenantRetryLoop BuildLoop(bool withSystemContext = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_creator);
        if (withSystemContext)
        {
            services.AddSingleton(_systemContext);
        }

        var sp = services.BuildServiceProvider();
        return new FailedTenantRetryLoop(sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FailedTenantRetryLoop>.Instance,
            new TenantSetupMetrics(_tracker, _registry), _tracker, _registry, _time);
    }

    /// <summary>Advances time tick by tick until the tenant's retry is due and the loop has run it.</summary>
    private async Task RunOneRetryCycleAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "retry cycle did not finish in time");
            _time.Advance(Tick);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OneRetryCycle_RecreatesTheMissingUser_AndTheSetupCompletes()
    {
        var userExists = false;
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x")).ReturnsLazily(() =>
        {
            _calls.Add("ensure");
            var created = !userExists;
            userExists = true;
            return Task.FromResult(created
                ? TenantDatabaseUserEnsureResult.Created
                : TenantDatabaseUserEnsureResult.AlreadyPresent);
        });
        // The setup of the retry succeeds only once the user exists (SCRAM failure otherwise) and then leaves the queue.
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).ReturnsLazily(() =>
        {
            _calls.Add("retry");
            if (userExists)
            {
                _tracker.Remove("x");
            }

            return Task.CompletedTask;
        });
        _tracker.RecordFailure("x", "MongoAuthenticationException: Unable to authenticate using sasl protocol mechanism SCRAM-SHA-1");

        var loop = BuildLoop();
        loop.Start();
        try
        {
            await RunOneRetryCycleAsync(() => _tracker.Count == 0);
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(["ensure", "retry"], _calls.Take(2));
        Assert.Equal(0, _tracker.Count);
    }

    [Fact]
    public async Task WhenTheUserCheckFails_TheRetryRunsAnyway()
    {
        A.CallTo(() => _systemContext.EnsureTenantDatabaseUserAsync("x"))
            .Throws(new InvalidOperationException("no admin credentials"));
        var retried = false;
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).ReturnsLazily(() =>
        {
            retried = true;
            _tracker.Remove("x");
            return Task.CompletedTask;
        });
        _tracker.RecordFailure("x", "e");

        var loop = BuildLoop();
        loop.Start();
        try
        {
            await RunOneRetryCycleAsync(() => retried);
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(retried);
    }

    [Fact]
    public async Task WithoutASystemContext_TheRetryRunsAsBefore()
    {
        var retried = false;
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).ReturnsLazily(() =>
        {
            retried = true;
            _tracker.Remove("x");
            return Task.CompletedTask;
        });
        _tracker.RecordFailure("x", "e");

        var loop = BuildLoop(withSystemContext: false);
        loop.Start();
        try
        {
            await RunOneRetryCycleAsync(() => retried);
        }
        finally
        {
            await loop.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(retried);
    }
}
