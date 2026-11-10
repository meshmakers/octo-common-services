using System.Diagnostics;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests.Services;

/// <summary>
///     AB#4690 — the per-tenant loop of the startup initialization used to be unguarded, so the first
///     tenant whose setup threw aborted it: every remaining tenant was skipped and the host start failed
///     with an AggregateException. A single broken tenant (e.g. one whose database was briefly unreachable
///     right after a delete + recreate) could therefore leave a whole service unprovisioned or
///     crash-looping.
/// </summary>
public class DefaultConfigurationInitializationServiceTests
{
    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IDefaultConfigurationCreatorService _creator = A.Fake<IDefaultConfigurationCreatorService>();

    public DefaultConfigurationInitializationServiceTests()
    {
        A.CallTo(() => _systemContext.TenantId).Returns("octosystem");
        A.CallTo(() => _systemContext.IsSystemTenantExistingAsync()).Returns(true);
        A.CallTo(() => _systemContext.GetAdminSessionAsync()).Returns(A.Fake<IOctoAdminSession>());
    }

    [Fact]
    public async Task OneFailingTenant_DoesNotStopTheOthers_NorFailTheHostStart()
    {
        StubChildTenants("tenant-a", "broken", "tenant-b");
        A.CallTo(() => _creator.SetupAsync("broken")).ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator);

        await sut.InitializeAsync();

        A.CallTo(() => _creator.SetupAsync("tenant-a")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _creator.SetupAsync("broken")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _creator.SetupAsync("tenant-b")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AFailingSystemTenant_StillFailsTheHostStart()
    {
        // Deliberately not guarded: without the system tenant nothing works at all, so this must stay fatal
        // instead of letting a broken instance come up looking healthy.
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem")).ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InitializeAsync());
    }

    /// <summary>
    ///     AB#8677: another service upgraded a CK model in place while this one started; the system tenant setup failed
    ///     only because the dependency had no Available version at that moment (the engine marks such a failure). The
    ///     setup is retried and the host comes up.
    /// </summary>
    [Fact]
    public async Task SystemTenantFailing_BecauseACkModelIsBeingImported_IsRetried_AndTheHostStarts()
    {
        StubChildTenants("tenant-a");
        A.CallTo(() => _creator.SetupAsync("octosystem"))
            .ThrowsAsync(ImportInProgress()).Twice()
            .Then.Returns(Task.CompletedTask);

        var sut = NewSut();

        await sut.InitializeAsync();

        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappened(3, Times.Exactly);
        A.CallTo(() => _creator.SetupAsync("tenant-a")).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    ///     AB#8677: the retry is bounded by a total deadline, not by an attempt count; an import that never ends still
    ///     fails the host start, with the original error.
    /// </summary>
    [Fact]
    public async Task SystemTenantFailing_BecauseACkModelIsBeingImported_FailsTheHostStartWhenTheDeadlineExpires()
    {
        StubChildTenants();
        var original = ImportInProgress();
        A.CallTo(() => _creator.SetupAsync("octosystem")).ThrowsAsync(original);

        var sut = NewSut();
        sut.SystemTenantRetryDeadline = TimeSpan.FromMilliseconds(300);
        sut.SystemTenantRetryDelay = TimeSpan.FromMilliseconds(20);

        var started = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InitializeAsync());
        started.Stop();

        Assert.Same(original, failure);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(300), $"gave up after {started.Elapsed}");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappened(2, Times.OrMore);
    }

    /// <summary>AB#8677: the deadline is time, so a slow attempt (the engine itself waits up to two minutes) counts.</summary>
    [Fact]
    public async Task SystemTenantFailing_AfterASlowAttemptThatUsedUpTheDeadline_IsNotRetried()
    {
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem")).ReturnsLazily(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            throw ImportInProgress();
        });

        var sut = NewSut();
        sut.SystemTenantRetryDeadline = TimeSpan.FromMilliseconds(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InitializeAsync());

        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappenedOnceExactly();
    }

    /// <summary>AB#8677: a host that stops while the system tenant setup waits for the next attempt ends the wait at once.</summary>
    [Fact]
    public async Task SystemTenantRetry_EndsTheWait_WhenTheHostStops()
    {
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem")).ThrowsAsync(ImportInProgress());
        using var stopping = new CancellationTokenSource();
        var lifetime = A.Fake<IHostApplicationLifetime>();
        A.CallTo(() => lifetime.ApplicationStopping).Returns(stopping.Token);

        var sut = new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator, lifetime)
        {
            SystemTenantRetryDelay = TimeSpan.FromMinutes(10)
        };

        var started = Stopwatch.StartNew();
        var run = sut.InitializeAsync();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappenedOnceExactly();
    }

    /// <summary>AB#8677: the engine's mark inside an AggregateException (e.g. from a parallel CK import) is retried too.</summary>
    [Fact]
    public async Task SystemTenantFailing_WithTheMarkInsideAnAggregateException_IsRetried()
    {
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem"))
            .ThrowsAsync(new AggregateException(new InvalidOperationException("other"), ImportInProgress())).Once()
            .Then.Returns(Task.CompletedTask);

        var sut = NewSut();

        await sut.InitializeAsync();

        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappened(2, Times.Exactly);
    }

    /// <summary>AB#8677: a real error of the system tenant setup is not retried; it fails the host start at once.</summary>
    [Fact]
    public async Task SystemTenantFailing_WithARealError_IsNotRetried()
    {
        StubChildTenants();
        A.CallTo(() => _creator.SetupAsync("octosystem"))
            .ThrowsAsync(new InvalidOperationException("Dependencies 'X-[1.0,2.0)' cannot be resolved"));

        var sut = NewSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.InitializeAsync());

        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Classifier_FindsTheMark_InWrappedAndAggregatedExceptions_AndNothingElse()
    {
        var marked = ImportInProgress();

        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(marked));
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new InvalidOperationException("setup failed", marked)));
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new AggregateException(new InvalidOperationException("other"), marked)));
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new InvalidOperationException("setup failed", new AggregateException(marked))));
        Assert.Equal("System.Bot", CkModelImportInProgressClassifier.DescribeModels(
            new InvalidOperationException("setup failed", marked)));
        Assert.Equal("System.Bot", CkModelImportInProgressClassifier.DescribeModels(
            new AggregateException(new InvalidOperationException("other"), marked)));
        Assert.Equal("System.Bot", CkModelImportInProgressClassifier.DescribeModels(
            new InvalidOperationException("setup failed", new AggregateException(marked))));
        Assert.Null(CkModelImportInProgressClassifier.DescribeModels(
            new AggregateException(new InvalidOperationException("other"))));
        Assert.Equal("Meshmakers.Octo.CkModelImportInProgress", CkModelImportInProgressClassifier.ExceptionDataKey);

        Assert.False(CkModelImportInProgressClassifier.IsCkModelImportInProgress(null));
        Assert.False(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new InvalidOperationException("Dependencies 'System.Bot-[3.4,4.0)' cannot be resolved (installed: 3.6.0 Importing)")));
    }

    /// <summary>
    ///     AB#8677: another service holds the CK model import lock longer than the engine waits for it (e.g. a long
    ///     System.Communication import). The engine throws a plain <see cref="TimeoutException" /> without the mark; the
    ///     setup is retried within the same deadline and the host comes up.
    /// </summary>
    [Fact]
    public async Task SystemTenantFailing_BecauseTheCkModelImportLockTimedOut_IsRetried_AndTheHostStarts()
    {
        StubChildTenants("tenant-a");
        A.CallTo(() => _creator.SetupAsync("octosystem"))
            .ThrowsAsync(ImportLockTimeout()).Once()
            .Then.Returns(Task.CompletedTask);

        var sut = NewSut();

        await sut.InitializeAsync();

        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappened(2, Times.Exactly);
        A.CallTo(() => _creator.SetupAsync("tenant-a")).MustHaveHappenedOnceExactly();
    }

    /// <summary>AB#8677: any other <see cref="TimeoutException" /> is a real error and still fails the host start at once.</summary>
    [Fact]
    public async Task SystemTenantFailing_WithAnUnrelatedTimeout_IsNotRetried()
    {
        StubChildTenants();
        var unrelated = new TimeoutException("A timeout occurred after 30000ms selecting a server.");
        A.CallTo(() => _creator.SetupAsync("octosystem")).ThrowsAsync(unrelated);

        var sut = NewSut();

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => sut.InitializeAsync());

        Assert.Same(unrelated, failure);
        A.CallTo(() => _creator.SetupAsync("octosystem")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void Classifier_FindsTheImportLockTimeout_OnlyByTypeSourceAndLockId()
    {
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(ImportLockTimeout()));
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new InvalidOperationException("import failed", ImportLockTimeout())));
        Assert.True(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new AggregateException(new InvalidOperationException("other"), ImportLockTimeout())));
        Assert.Equal("System.Communication", CkModelImportInProgressClassifier.DescribeModels(
            new InvalidOperationException("import failed", ImportLockTimeout())));

        // Same message, but not thrown by the engine.
        Assert.False(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new TimeoutException(ImportLockTimeout().Message)));
        // Engine timeout of another lock (index update), not the import lock.
        Assert.False(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new TimeoutException("Could not acquire distributed lock 'index_update_lock' after 90 seconds.")
                { Source = CkModelImportInProgressClassifier.EngineAssemblyName }));
        // Import-lock message, but not a TimeoutException.
        Assert.False(CkModelImportInProgressClassifier.IsCkModelImportInProgress(
            new InvalidOperationException(ImportLockTimeout().Message)
                { Source = CkModelImportInProgressClassifier.EngineAssemblyName }));
    }

    /// <summary>
    ///     The match relies on <see cref="Exception.Source" /> naming the assembly of the throwing method also after the
    ///     exception crossed awaits and a <c>throw;</c> rethrow, as the engine's lock wait does.
    /// </summary>
    [Fact]
    public async Task ExceptionSource_NamesTheThrowingAssembly_AfterAwaitAndRethrow()
    {
        var failure = await Assert.ThrowsAsync<TimeoutException>(ThrowAfterAwaitAndRethrowAsync);

        Assert.Equal(typeof(DefaultConfigurationInitializationServiceTests).Assembly.GetName().Name, failure.Source);
    }

    private static async Task ThrowAfterAwaitAndRethrowAsync()
    {
        try
        {
            await Task.Yield();
            throw new TimeoutException("lock wait");
        }
        catch
        {
            await Task.Yield();
            throw;
        }
    }

    /// <summary>The engine's import-lock wait timeout as <c>RepositoryDistributedLockService.AcquireLockAsync</c> throws it.</summary>
    private static TimeoutException ImportLockTimeout()
    {
        return new TimeoutException(
            "Could not acquire distributed lock 'ck_model_import_System.Communication' after 90 seconds. " +
            "Current lock expires at 2026-11-09T10:00:00.0000000Z")
        {
            Source = CkModelImportInProgressClassifier.EngineAssemblyName
        };
    }

    private DefaultConfigurationInitializationService NewSut()
    {
        return new DefaultConfigurationInitializationService(
            NullLogger<DefaultConfigurationInitializationService>.Instance, _systemContext, _creator)
        {
            SystemTenantRetryDelay = TimeSpan.Zero
        };
    }

    /// <summary>A failure as the CK engine marks it (key of <c>CkModelImportInProgress.ExceptionDataKey</c>).</summary>
    private static InvalidOperationException ImportInProgress()
    {
        var exception = new InvalidOperationException(
            "Dependencies 'System.Bot-[3.4,4.0)' cannot be resolved against the tenant: no Available version of " +
            "System.Bot (installed: 3.6.0 Importing)");
        exception.Data["Meshmakers.Octo.CkModelImportInProgress"] = "System.Bot";
        return exception;
    }

    private void StubChildTenants(params string[] tenantIds)
    {
        var resultSet = A.Fake<IResultSet<OctoTenant>>();
        A.CallTo(() => resultSet.Items)
            .Returns(tenantIds.Select(t => new OctoTenant(t, t)).ToList());
        A.CallTo(() => _systemContext.GetAllTenantsAsync(A<IOctoAdminSession>._, A<int?>._, A<int?>._))
            .Returns(resultSet);
    }
}
