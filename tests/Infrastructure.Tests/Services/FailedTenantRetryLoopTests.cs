using FakeItEasy;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#7271 — a tenant queued for retry during the startup pass is retried at its announced time, even while the
///     pass is still busy with other tenants. Before, the retry loop was a hosted service that only started after
///     <c>HostedInitializer.StartAsync</c> (= the whole pass) returned (test-2, 2026-10-22: announced "+30 s",
///     actual ~5 min).
/// </summary>
public class FailedTenantRetryLoopTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 1, 33, 0, TimeSpan.Zero));
    private readonly TenantSetupRetryTracker _tracker;
    private readonly IDefaultConfigurationCreatorService _creator = A.Fake<IDefaultConfigurationCreatorService>();
    private readonly List<(string Tenant, DateTimeOffset At)> _retries = [];
    private readonly TaskCompletionSource _retried = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FailedTenantRetryLoopTests()
    {
        _tracker = new TenantSetupRetryTracker(_time);
        // What RetryFailedTenantsAsync does for the in-memory queue: re-run the setup of every due tenant.
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).Invokes(() =>
        {
            foreach (var due in _tracker.GetDue())
            {
                lock (_retries)
                {
                    _retries.Add((due.TenantId, _time.GetUtcNow()));
                }

                _tracker.Remove(due.TenantId);
                _retried.TrySetResult();
            }
        }).Returns(Task.CompletedTask);
    }

    private ServiceProvider BuildProvider(IAsyncInitializationService initializer, bool withRetryLoop = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_creator);
        services.AddTransient(_ => initializer);
        if (withRetryLoop)
        {
            services.AddSingleton(_tracker);
            services.AddSingleton(new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time));
            services.AddSingleton<TenantSetupMetrics>();
            services.AddSingleton<FailedTenantRetryLoop>();
        }

        return services.BuildServiceProvider();
    }

    private static IHostedService NewInitializer(IServiceProvider sp)
    {
        var type = typeof(FailedTenantRetryLoop).Assembly.GetType(
            "Meshmakers.Octo.Services.Infrastructure.Initialization.HostedInitializer")!;
        return (IHostedService)Activator.CreateInstance(type, sp)!;
    }

    /// <summary>An initialization pass that queues tenant "a" as failed, then stays busy with "other tenants".</summary>
    private sealed class BusyPass(TenantSetupRetryTracker tracker, TaskCompletionSource release,
        TaskCompletionSource failedQueued) : IAsyncInitializationService
    {
        public int Order => 10;

        public async Task InitializeAsync()
        {
            tracker.RecordFailure("a", "primary election");
            failedQueued.SetResult();
            await release.Task; // the slowest remaining tenant
        }
    }

    [Fact]
    public async Task ARetryDueBeforeThePassEnds_RunsWhileThePassIsStillRunning_AtTheAnnouncedTime()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sp = BuildProvider(new BusyPass(_tracker, release, failedQueued));
        var hosted = NewInitializer(sp);

        var start = hosted.StartAsync(TestContext.Current.CancellationToken);
        await failedQueued.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var announced = _tracker.Snapshot().Single().NextAttemptAt; // "next retry" of the log line

        // Time passes while the pass is still running: first tick at +30 s.
        _time.Advance(Tick);
        await _retried.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(start.IsCompleted, "the startup pass must still be running when the retry happens");
        var retry = Assert.Single(_retries);
        Assert.Equal("a", retry.Tenant);
        Assert.InRange(retry.At - announced, TimeSpan.Zero, Tick);

        release.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARetryDueAfterTheFirstTick_RunsAtMostOneTickLate()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sp = BuildProvider(new BusyPass(_tracker, release, failedQueued));
        var hosted = NewInitializer(sp);

        var start = hosted.StartAsync(TestContext.Current.CancellationToken);
        await failedQueued.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10)); // the tenant fails 10 s into the pass...
        _tracker.RecordFailure("b", "primary election"); // ...announced for +40 s

        var announced = _tracker.Snapshot().Single(e => e.TenantId == "b").NextAttemptAt;
        _time.Advance(TimeSpan.FromSeconds(20)); // tick at +30 s: "b" not due yet, "a" is
        await _retried.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(Tick); // tick at +60 s: "b" is due

        await WaitUntilAsync(() => { lock (_retries) { return _retries.Count == 2; } });
        var retryB = _retries.Single(r => r.Tenant == "b");
        Assert.InRange(retryB.At - announced, TimeSpan.Zero, Tick);
        Assert.False(start.IsCompleted);

        release.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASystemTenantFailure_StillFailsTheHostStart_AndStopsTheLoop()
    {
        var failing = A.Fake<IAsyncInitializationService>();
        A.CallTo(() => failing.Order).Returns(10);
        A.CallTo(() => failing.InitializeAsync()).Throws(new InvalidOperationException("octosystem unreachable"));
        using var sp = BuildProvider(failing);
        var hosted = NewInitializer(sp);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => hosted.StartAsync(TestContext.Current.CancellationToken));
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions.Single());

        // The loop is stopped: a tick after the failed start retries nothing.
        _tracker.RecordFailure("x", "e");
        _time.Advance(Tick * 2);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).MustNotHaveHappened();
    }

    [Fact]
    public async Task StopAsync_ReturnsAtTheHostDeadline_EvenWhenARetryTickIsStuck()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _creator.RetryFailedTenantsAsync()).Invokes(() => entered.TrySetResult()).Returns(stuck.Task);
        using var sp = BuildProvider(A.Fake<IAsyncInitializationService>());
        var hosted = NewInitializer(sp);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        _time.Advance(Tick);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await hosted.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        stuck.SetResult(); // the stuck tick finishes later; nothing may blow up
    }

    [Fact]
    public async Task WithoutTheInfrastructureRegistrations_TheInitializerRunsAsBefore()
    {
        var pass = A.Fake<IAsyncInitializationService>();
        A.CallTo(() => pass.Order).Returns(10);
        using var sp = BuildProvider(pass, withRetryLoop: false);
        var hosted = NewInitializer(sp);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() => pass.InitializeAsync()).MustHaveHappenedOnceExactly();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
