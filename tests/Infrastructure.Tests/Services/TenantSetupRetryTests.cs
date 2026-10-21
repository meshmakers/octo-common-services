using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#7249 — a tenant whose setup fails at startup (staging-1, 2026-10-21: MongoDB primary election during an
///     AKS node-pool upgrade) must be retried until it succeeds. The log line "this tenant will be retried in the
///     background" used to hold only for services that wired the durable MongoDB-backed retry store; the
///     communication controller and bot-services did not, and where the store was wired its write timed out
///     during the same outage. The retry is now an in-memory queue that needs neither.
/// </summary>
public class TenantSetupRetryTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 11, 50, 0, TimeSpan.Zero));
    private readonly TenantSetupRetryTracker _tracker;

    public TenantSetupRetryTests()
    {
        _tracker = new TenantSetupRetryTracker(_time);
    }

    // ---- backoff ----------------------------------------------------------------------------------------------

    [Fact]
    public void Backoff_DoublesFromTheInitialDelay_AndIsCappedAtFiveMinutes()
    {
        var options = new TenantSetupRetryOptions();

        var delays = Enumerable.Range(1, 8).Select(options.DelayAfter).Select(d => (int)d.TotalSeconds).ToArray();

        Assert.Equal([30, 60, 120, 240, 300, 300, 300, 300], delays);
    }

    [Fact]
    public void Backoff_DoesNotOverflow_ForHugeAttemptCounts()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new TenantSetupRetryOptions().DelayAfter(int.MaxValue));
    }

    [Fact]
    public void Tracker_SchedulesTheNextAttemptWithBackoff_AndKeepsTheFirstFailure()
    {
        var first = _tracker.RecordFailure("sbeg", "timeout");
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = _tracker.RecordFailure("sbeg", "timeout again");

        Assert.Equal(1, first.Attempts);
        Assert.Equal(first.FirstFailedAt + TimeSpan.FromSeconds(30), first.NextAttemptAt);
        Assert.Equal(2, second.Attempts);
        Assert.Equal(first.FirstFailedAt, second.FirstFailedAt);
        Assert.Equal(second.LastFailedAt + TimeSpan.FromSeconds(60), second.NextAttemptAt);
        Assert.Equal("timeout again", second.LastError);
    }

    [Fact]
    public void Tracker_ReportsAnEntryAsDue_OnlyOnceItsBackoffElapsed()
    {
        _tracker.RecordFailure("sbeg", "timeout");

        Assert.Empty(_tracker.GetDue());
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(_tracker.GetDue());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["sbeg"], _tracker.GetDue().Select(e => e.TenantId));
    }

    // ---- the retry itself (no store, store that fails) ---------------------------------------------------------

    [Fact]
    public async Task FailedSetup_IsRetriedInTheBackground_WithoutAnyRetryStore()
    {
        var sut = new FlakyCreator(_tracker, mongoUp: false);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));
        Assert.DoesNotContain("sbeg", sut.Enabled);

        // too early: nothing happens
        await sut.RetryFailedTenantsAsync();
        Assert.Equal(1, sut.Attempts["sbeg"]);

        // MongoDB is back, the backoff elapses, the next tick enables the tenant
        sut.MongoUp = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync();

        Assert.Contains("sbeg", sut.Enabled);
        Assert.Equal(0, _tracker.Count);
    }

    [Fact]
    public async Task FailedSetup_IsStillRetried_WhenTheRetryStoreWriteTimesOutToo()
    {
        // The durable store lives in the MongoDB that is down: its RecordFailure/Clear throw.
        var store = A.Fake<ITenantSetupRetryStore>();
        A.CallTo(() => store.RecordFailureAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("A timeout occurred after 29999ms selecting a server"));
        A.CallTo(() => store.ClearAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("store down"));
        A.CallTo(() => store.TryClaimAsync(A<string>._, A<string>._, A<TimeSpan>._, A<TimeSpan>._, A<int>._,
                A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("store down"));
        var sut = new FlakyCreator(_tracker, mongoUp: false, store);

        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));
        Assert.Equal(1, _tracker.Count);

        sut.MongoUp = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync();

        Assert.Contains("sbeg", sut.Enabled);
        Assert.Equal(0, _tracker.Count);
    }

    [Fact]
    public async Task Retry_NeverGivesUp_AndBacksOffToTheCap()
    {
        var sut = new FlakyCreator(_tracker, mongoUp: false);
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));

        // 30 retries: far beyond the former 10 attempts / ~5 minutes
        for (var i = 0; i < 30; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await sut.RetryFailedTenantsAsync();
        }

        var entry = Assert.Single(_tracker.Snapshot());
        Assert.Equal(31, entry.Attempts);
        Assert.Equal(TimeSpan.FromMinutes(5), entry.NextAttemptAt - entry.LastFailedAt);

        sut.MongoUp = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await sut.RetryFailedTenantsAsync();
        Assert.Contains("sbeg", sut.Enabled);
    }

    [Fact]
    public async Task Retry_DoesNotRunBeforeTheBackoffElapsed_AfterAFurtherFailure()
    {
        var sut = new FlakyCreator(_tracker, mongoUp: false);
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));

        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync(); // fails again -> next delay 60 s
        Assert.Equal(2, sut.Attempts["sbeg"]);

        _time.Advance(TimeSpan.FromSeconds(59));
        await sut.RetryFailedTenantsAsync();
        Assert.Equal(2, sut.Attempts["sbeg"]);

        _time.Advance(TimeSpan.FromSeconds(1));
        await sut.RetryFailedTenantsAsync();
        Assert.Equal(3, sut.Attempts["sbeg"]);
    }

    [Fact]
    public async Task Retry_DropsATenantThatNoLongerExists()
    {
        var sut = new FlakyCreator(_tracker, mongoUp: true) { FailWith = id => TenantException.TenantDoesNotExist(id) };
        await Assert.ThrowsAsync<TenantException>(() => sut.SetupAsync("gone"));

        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync();

        Assert.Equal(0, _tracker.Count);
        Assert.Equal(2, sut.Attempts["gone"]);

        _time.Advance(TimeSpan.FromMinutes(10));
        await sut.RetryFailedTenantsAsync();
        Assert.Equal(2, sut.Attempts["gone"]);
    }

    [Fact]
    public async Task OneTenantStillFailing_DoesNotBlockTheRetryOfAnother()
    {
        var sut = new FlakyCreator(_tracker, mongoUp: false);
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("eegroider"));

        sut.FailWith = id => id == "sbeg" ? new TimeoutException("still down") : null;
        sut.MongoUp = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync();

        Assert.Contains("eegroider", sut.Enabled);
        Assert.DoesNotContain("sbeg", sut.Enabled);
        Assert.Equal(["sbeg"], _tracker.Snapshot().Select(e => e.TenantId));
    }

    [Fact]
    public async Task SetupSkippedBecauseOfADeleteInProgress_LeavesTheRetryQueue()
    {
        var lifecycle = A.Fake<ITenantLifecycleStore>();
        var sut = new FlakyCreator(_tracker, mongoUp: false, lifecycleStore: lifecycle);
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));

        A.CallTo(() => lifecycle.GetAsync("sbeg", A<CancellationToken>._))
            .Returns(new TenantLifecycleRecord { TenantId = "sbeg", State = TenantLifecycleState.Deleting });
        _time.Advance(TimeSpan.FromSeconds(30));
        await sut.RetryFailedTenantsAsync();

        Assert.Equal(0, _tracker.Count);
    }

    // ---- the incident: startup loop, tenant fails, MongoDB returns, no restart --------------------------------

    [Fact]
    public async Task Startup_TenantFailsWhileMongoIsUnavailable_BecomesEnabledAfterMongoReturns_WithoutRestart()
    {
        // staging-1 2026-10-21: 7 tenants, 'sbeg' and 'eegroider' fail during the primary election, the rest passes.
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.TenantId).Returns("octosystem");
        A.CallTo(() => systemContext.IsSystemTenantExistingAsync()).Returns(true);
        A.CallTo(() => systemContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        var tenants = new[] { "voestalpine", "sbeg", "eegroider", "meshtest" };
        A.CallTo(() => resultSet.Items).Returns(tenants.Select(t => new OctoTenant(t, t)).ToList());
        A.CallTo(() => systemContext.GetAllTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Returns(resultSet);

        var startup = new FlakyCreator(_tracker, mongoUp: true)
        {
            FailWith = id => id is "sbeg" or "eegroider" ? new TimeoutException("selecting a server, Mode: Primary") : null
        };
        var init = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, systemContext, startup);

        await init.InitializeAsync(); // does not throw: the host starts, two tenants are not enabled

        Assert.DoesNotContain("sbeg", startup.Enabled);
        Assert.DoesNotContain("eegroider", startup.Enabled);
        Assert.Contains("voestalpine", startup.Enabled);

        // The background service resolves a fresh creator per tick; the queue is shared (singleton).
        var backgroundTick = new FlakyCreator(_tracker, mongoUp: true);

        _time.Advance(TimeSpan.FromSeconds(30));
        await backgroundTick.RetryFailedTenantsAsync();

        Assert.Contains("sbeg", backgroundTick.Enabled);
        Assert.Contains("eegroider", backgroundTick.Enabled);
        Assert.Equal(0, _tracker.Count);
    }

    // ---- health and metrics ------------------------------------------------------------------------------------

    private TenantSetupHealthCheck CreateHealthCheck(FailedTenantRegistry? registry = null,
        TenantSetupRetryOptions? options = null)
        => new(_tracker, registry ?? new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time),
            Options.Create(options ?? new TenantSetupRetryOptions()), _time);

    [Fact]
    public async Task Health_IsHealthy_WhenNoTenantIsFailing()
    {
        var result = await CreateHealthCheck().CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Health_IsHealthy_WhileAFailureIsWithinTheThreshold_AndDegradedBeyondIt()
    {
        var check = CreateHealthCheck(options: new TenantSetupRetryOptions { DegradedAfter = TimeSpan.FromMinutes(10) });
        _tracker.RecordFailure("sbeg", "timeout");

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        var degraded = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Contains("sbeg", degraded.Description);
        Assert.Contains("sbeg", degraded.Data.Keys);
    }

    [Fact]
    public async Task Health_ThresholdIsConfigurable()
    {
        var check = CreateHealthCheck(options: new TenantSetupRetryOptions { DegradedAfter = TimeSpan.FromMinutes(1) });
        _tracker.RecordFailure("sbeg", "timeout");
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Health_RecoversWhenTheTenantIsSetUp()
    {
        var check = CreateHealthCheck();
        var sut = new FlakyCreator(_tracker, mongoUp: false);
        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync("sbeg"));
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);

        sut.MongoUp = true;
        await sut.RetryFailedTenantsAsync();

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Health_CoversTenantsWhoseDeferredStartFailed()
    {
        var registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
        registry.Add("sbeg");
        _time.Advance(TimeSpan.FromMinutes(11));

        var result = await CreateHealthCheck(registry).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void Metrics_PublishOneSeriesPerFailingTenant_AndDropItOnRecovery()
    {
        var registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
        registry.Add("start-failed");
        _tracker.RecordFailure("sbeg", "timeout");
        _time.Advance(TimeSpan.FromSeconds(90));

        var seconds = TenantSetupMetrics
            .Observe(_tracker, registry, _time, (first, now) => (now - first).TotalSeconds)
            .ToDictionary(m => (string)m.Tags.ToArray().Single(t => t.Key == "octo.tenant.id").Value!,
                m => (m.Value, (string)m.Tags.ToArray().Single(t => t.Key == "octo.tenant_setup.phase").Value!));

        Assert.Equal((90.0, "setup"), seconds["sbeg"]);
        Assert.Equal((90.0, "start"), seconds["start-failed"]);

        _tracker.Remove("sbeg");
        registry.Remove("start-failed");
        Assert.Empty(TenantSetupMetrics.Observe(_tracker, registry, _time, (_, _) => 1));
    }

    [Fact]
    public void Metrics_MeterNameMatchesTheOneRegisteredByObservability()
    {
        // ObservabilityBuilder registers this meter by string (an unregistered meter drops every measurement).
        Assert.Equal("Meshmakers.Octo.TenantSetup", TenantSetupMetrics.MeterName);
    }

    // ---- registry: deferred-start failures back off and never stop --------------------------------------------

    [Fact]
    public void Registry_BacksOffExponentially_AndOnlyReportsDueTenants()
    {
        var registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
        registry.Add("sbeg");

        Assert.Empty(registry.GetDue());
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(registry.GetDue());

        Assert.Equal(1, registry.IncrementRetryCount("sbeg"));
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(registry.GetDue());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(registry.GetDue());
    }

    [Fact]
    public void Registry_ReportsExhaustionOnlyOncePerRegistration()
    {
        var registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
        registry.Add("sbeg");

        Assert.True(registry.TryMarkExhaustionReported("sbeg"));
        Assert.False(registry.TryMarkExhaustionReported("sbeg"));

        registry.Remove("sbeg");
        registry.Add("sbeg");
        Assert.True(registry.TryMarkExhaustionReported("sbeg"));
    }

    [Fact]
    public async Task DeferredStartRetry_KeepsRetrying_PastTheFormerTenAttemptLimit()
    {
        var registry = new FailedTenantRegistry(Options.Create(new TenantSetupRetryOptions()), _time);
        registry.Add("sbeg");
        var sut = new StartFailingCreator(registry, _tracker);

        for (var i = 0; i < 25; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await sut.RetryFailedTenantsAsync();
        }

        Assert.True(registry.HasFailedTenants);
        Assert.Equal(25, sut.StartAttempts);
        Assert.Equal(1, sut.ExhaustedReports);

        sut.StartSucceeds = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await sut.RetryFailedTenantsAsync();
        Assert.False(registry.HasFailedTenants);
    }

    // ---- test doubles ------------------------------------------------------------------------------------------

    private sealed class FlakyCreator : DefaultConfigurationCreatorServiceBase
    {
        public FlakyCreator(TenantSetupRetryTracker tracker, bool mongoUp, ITenantSetupRetryStore? store = null,
            ITenantLifecycleStore? lifecycleStore = null)
            : base(NullLogger<DefaultConfigurationCreatorServiceBase>.Instance, tenantSetupRetryStore: store,
                tenantLifecycleStore: lifecycleStore, setupRetryTracker: tracker)
        {
            MongoUp = mongoUp;
        }

        /// <summary>When false every setup throws a Mongo-style timeout.</summary>
        public bool MongoUp { get; set; }

        /// <summary>Per-tenant failure override (null = no failure) applied while <see cref="MongoUp"/>.</summary>
        public Func<string, Exception?>? FailWith { get; set; }

        public HashSet<string> Enabled { get; } = [];

        public Dictionary<string, int> Attempts { get; } = [];

        protected override Task SetupTenantAsync(string tenantId)
        {
            Attempts[tenantId] = Attempts.GetValueOrDefault(tenantId) + 1;
            if (!MongoUp)
            {
                throw new TimeoutException("A timeout occurred after 29999ms selecting a server. Mode: Primary");
            }

            var failure = FailWith?.Invoke(tenantId);
            if (failure is not null)
            {
                throw failure;
            }

            Enabled.Add(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class StartFailingCreator : DefaultConfigurationCreatorServiceStandardized
    {
        public StartFailingCreator(FailedTenantRegistry registry, TenantSetupRetryTracker tracker)
            : base(
                NullLogger<DefaultConfigurationCreatorServiceStandardized>.Instance,
                A.Fake<ISystemContext>(),
                A.Fake<Meshmakers.Octo.Common.DistributionEventHub.Services.ICommandClient<
                    Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.CreateIdentityDataCommandRequest>>(),
                identityDataVersionKey: "test-id-data-version",
                expectedIdentityDataVersion: 1,
                failedTenantRegistry: registry,
                setupRetryTracker: tracker)
        {
        }

        public bool StartSucceeds { get; set; }
        public int StartAttempts { get; private set; }
        public int ExhaustedReports { get; private set; }

        protected override Task SetupTenantAsync(string tenantId) => Task.CompletedTask;

        protected override Task StartTenantAsync(string tenantId)
        {
            StartAttempts++;
            return StartSucceeds ? Task.CompletedTask : throw new TimeoutException("start failed");
        }

        protected override Task OnTenantRetriesExhaustedAsync(string tenantId, int retryCount)
        {
            ExhaustedReports++;
            return Task.CompletedTask;
        }
    }
}
