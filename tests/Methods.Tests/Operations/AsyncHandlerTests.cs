using System.Diagnostics.Metrics;
using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Metrics;
using Meshmakers.Octo.Services.Methods.Operations;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Operations;

/// <summary>
///     AB#8443: a handler returns <c>Accepted</c>; the host records the operation before the envelope leaves, runs the work
///     once and writes progress and the final state.
/// </summary>
public class AsyncHandlerTests
{
    private static Task<CkMethodOutcome<UserChangePasswordResult>> Done(string value = "exported") =>
        Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult { Value = value });

    [Fact]
    public async Task Accepted_RecordExistsBeforeTheEnvelopeIsReturned_AndEnvelopeCarriesTheHandle()
    {
        var gate = new GatedStore(new CkMethodInMemoryOperationStore(TimeProvider.System));
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Done()), store: gate);

        var call = fixture.InvokeAsync();
        await gate.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // the store write is in progress: the caller has no envelope yet
        Assert.False(call.IsCompleted);
        gate.Release.SetResult();
        var response = await call;

        Assert.True(response.Success);
        Assert.True(response.IsAccepted);
        Assert.Null(response.ResultJson);
        Assert.Equal(gate.CreatedOperationId, response.Operation!.OperationId);
        Assert.Equal(fixture.Time.GetUtcNow().UtcDateTime, response.Operation.AcceptedAtUtc);
        Assert.Equal(new CkMethodOperationOptions().RetryAfterSeconds, response.Operation.RetryAfterSeconds);
        Assert.Equal(fixture.Instance.InstanceId, response.HandlerInstanceId);
        Assert.True(gate.CreatedBeforeReturn);
    }

    [Fact]
    public async Task Work_ReportsProgress_ThenSucceeds_RecordShowsRunningThenSucceededWithResult()
    {
        var reported = new TaskCompletionSource();
        var proceed = new TaskCompletionSource();
        var fixture = AsyncOperationFixture.With(new ExportHandler(async (progress, ct) =>
        {
            await progress.ReportAsync(CkMethodOperationProgress.Create(50, "EXPORTING", ("done", "5")), ct);
            reported.SetResult();
            await proceed.Task.WaitAsync(ct);
            return new UserChangePasswordResult { Value = "exported" };
        }));

        var response = await fixture.InvokeAsync();
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var running = await fixture.Store.GetAsync("meshtest", response.Operation!.OperationId,
            TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodOperationState.Running, running!.State);
        Assert.Equal(50, running.Progress!.Percent);
        Assert.Equal("EXPORTING", running.Progress.StageCode);
        Assert.Null(running.ExpiresAtUtc);

        fixture.Time.Advance(TimeSpan.FromMinutes(3));
        proceed.SetResult();
        var done = await fixture.FinishedAsync(response);

        Assert.Equal(CkMethodOperationState.Succeeded, done.State);
        Assert.Equal("\"exported\"", done.ResultJson);
        var completedAt = fixture.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(completedAt, done.CompletedAtUtc);
        Assert.Equal(completedAt + CkMethodOperationOptions.DefaultRetention, done.ExpiresAtUtc);
        Assert.Equal(response.InvocationId, done.InvocationId);
        Assert.Equal(new CkMethodOperationOwner(HostFixture.Administrator.SubjectId, null), done.Owner);
    }

    [Fact]
    public async Task Work_FailsWithADeclaredError_RecordShowsFailedWithCodeAndParameters()
    {
        var fixture = AsyncOperationFixture.With(
            new ExportHandler((_, _) => Task.FromResult(
                CkMethodOutcome<UserChangePasswordResult>.Fail("EXPORT_FAILED", ("collection", "assets")))),
            AsyncOperationFixture.AsyncDefinition(errors: "EXPORT_FAILED"));

        var done = await fixture.FinishedAsync(await fixture.InvokeAsync());

        Assert.Equal(CkMethodOperationState.Failed, done.State);
        Assert.Equal("EXPORT_FAILED", done.Error!.Code);
        Assert.Equal("assets", done.Error.Parameters["collection"]);
        Assert.NotNull(done.ExpiresAtUtc);
    }

    [Fact]
    public async Task Work_FailsWithAnUndeclaredCode_IsHandlerFailed()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Task.FromResult(
            CkMethodOutcome<UserChangePasswordResult>.Fail("NOT_DECLARED"))));

        var done = await fixture.FinishedAsync(await fixture.InvokeAsync());

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, done.Error!.Code);
    }

    [Fact]
    public async Task Work_Throws_IsHandlerFailedWithCorrelationId_AndTheSecretIsNotLogged()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) =>
            throw new InvalidOperationException("boom " + TestData.Secret)));
        var request = fixture.Request();

        var done = await fixture.FinishedAsync(await fixture.InvokeAsync(request));

        Assert.Equal(CkMethodOperationState.Failed, done.State);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, done.Error!.Code);
        Assert.Equal(request.InvocationId.ToString(), done.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.DoesNotContain(TestData.Secret, fixture.AllLogText());
    }

    [Fact]
    public async Task Record_NeverContainsParametersTokenOrASensitiveResult()
    {
        var fixture = AsyncOperationFixture.With(
            new ExportHandler((_, _) => Done("result-" + TestData.Secret)),
            AsyncOperationFixture.AsyncDefinition(sensitiveResult: true));

        var done = await fixture.FinishedAsync(await fixture.InvokeAsync());
        var serialized = JsonSerializer.Serialize(done) + done;

        Assert.Equal(CkMethodOperationState.Succeeded, done.State);
        Assert.Null(done.ResultJson);
        Assert.DoesNotContain(TestData.Secret, serialized);
        Assert.DoesNotContain(TestData.OldSecret, serialized);
        Assert.DoesNotContain(TestData.Token, serialized);
        Assert.DoesNotContain(TestData.Secret, fixture.AllLogText());
    }

    [Fact]
    public async Task Accepted_ForAMethodNotDeclaredAsync_IsRefused_AndNothingIsRecorded()
    {
        var handler = new ExportHandler((_, _) => Done());
        var fixture = AsyncOperationFixture.With(handler, TestData.Definition());

        var response = await fixture.InvokeAsync();

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(0, fixture.InMemoryStore.Count);
    }

    [Fact]
    public async Task Accepted_OnAHostWithoutAddCkMethodOperations_IsRefused()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Done()), withOperations: false);

        var response = await fixture.InvokeAsync();

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Contains("AddCkMethodOperations", fixture.AllLogText());
    }

    [Fact]
    public async Task StoreDown_CallIsNotAccepted_AndTheWorkNeverStarts()
    {
        var started = 0;
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) =>
        {
            Interlocked.Increment(ref started);
            return Done();
        }), store: new DownStore());

        var response = await fixture.InvokeAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(CkMethodOperationReasons.StoreUnavailable,
            response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, started);
        Assert.Equal(0, fixture.Runner.RunningCount);
    }

    [Fact]
    public async Task ValidationRefusal_BeforeAccept_IsASynchronousAnswer_WithoutRecord()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Done(),
            _ => CkMethodOutcome<UserChangePasswordResult>.ValidationFailed("hint", "required")));

        var response = await fixture.InvokeAsync();

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(0, fixture.InMemoryStore.Count);
    }

    [Fact]
    public async Task OwnerOfAServiceCaller_IsTheClientId()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Done()),
            principal: new CkMethodPrincipal(null, [CkMethodAuthorizationOptions.DefaultAdministratorRoles.First()],
                [CkMethodAuthorizationOptions.ApiScope], "svc-client"));

        var done = await fixture.FinishedAsync(await fixture.InvokeAsync());

        Assert.Equal(new CkMethodOperationOwner(null, "svc-client"), done.Owner);
    }

    [Fact]
    public async Task HostStop_CancelsTheWork_WhichEndsAsHandlerLost()
    {
        var fixture = AsyncOperationFixture.With(new ExportHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new UserChangePasswordResult();
        }));
        var response = await fixture.InvokeAsync();

        await fixture.Runner.StopAsync(TestContext.Current.CancellationToken);
        var done = await fixture.FinishedAsync(response);

        Assert.Equal(CkMethodOperationState.Failed, done.State);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, done.Error!.Code);
        Assert.Equal(CkMethodOperationReasons.HandlerLost, done.Error.Parameters[CkMethodErrorParameters.Reason]);
    }

    [Fact]
    public async Task AfterHostStop_NewCallsAreNotAccepted_NothingIsRecorded_AndTheWorkNeverStarts()
    {
        var started = 0;
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) =>
        {
            Interlocked.Increment(ref started);
            return Done();
        }));
        await fixture.Runner.StopAsync(TestContext.Current.CancellationToken);

        var response = await fixture.InvokeAsync();

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(CkMethodOperationReasons.HostStopping, response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(0, fixture.InMemoryStore.Count);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task LostHandler_SweepEndsIt_TheWorkIsNeverStartedAgain_AndItsLateOutcomeIsDropped()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var runs = 0;
        var fixture = AsyncOperationFixture.With(new ExportHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref runs);
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return new UserChangePasswordResult { Value = "late" };
        }));
        var response = await fixture.InvokeAsync();
        var operationId = response.Operation!.OperationId;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // no heartbeat service runs in this test: the handler "died" from the platform's point of view
        fixture.Time.Advance(new CkMethodOperationOptions().StaleAfter + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await fixture.Runner.SweepAsync(TestContext.Current.CancellationToken));

        release.SetResult();
        var done = await fixture.FinishedAsync(response);

        Assert.Equal(CkMethodOperationState.Failed, done.State);
        Assert.Equal(CkMethodErrorCodes.MethodOutcomeUnknown, done.Error!.Code);
        Assert.Equal(CkMethodOperationReasons.HandlerLost, done.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Null(done.ResultJson);
        Assert.Equal(1, runs);
        Assert.Equal(0, await fixture.Runner.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(operationId, done.OperationId);
    }

    [Fact]
    public async Task Heartbeat_KeepsARunningOperationAlive()
    {
        var release = new TaskCompletionSource();
        var fixture = AsyncOperationFixture.With(new ExportHandler(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return new UserChangePasswordResult { Value = "ok" };
        }));
        var response = await fixture.InvokeAsync();
        var options = new CkMethodOperationOptions();

        for (var i = 0; i < 20; i++)
        {
            fixture.Time.Advance(options.HeartbeatInterval);
            await fixture.Runner.HeartbeatAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await fixture.Runner.SweepAsync(TestContext.Current.CancellationToken));
        release.SetResult();
        Assert.Equal(CkMethodOperationState.Succeeded, (await fixture.FinishedAsync(response)).State);
    }

    [Fact]
    public async Task Metrics_CountOperationsByState_WithoutTenantOrUserLabels()
    {
        var methodId = $"Test.Ops{Guid.NewGuid():N}/Thing.Export-1";
        var measurements = new List<(string Instrument, long Value, Dictionary<string, string?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CkMethodTelemetry.MeterName && instrument.Name == CkMethodMetrics.OperationsName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            var copy = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
            if (copy.GetValueOrDefault(CkMethodMetrics.MethodIdTag) == methodId)
            {
                lock (measurements)
                {
                    measurements.Add((i.Name, v, copy));
                }
            }
        });
        listener.Start();
        var fixture = AsyncOperationFixture.With(new ExportHandler((_, _) => Done()), methodId: methodId);

        await fixture.FinishedAsync(await fixture.InvokeAsync());

        lock (measurements)
        {
            Assert.Equal(["accepted", "running", "succeeded"],
                measurements.Select(m => m.Tags[CkMethodMetrics.StateTag]).ToArray());
            Assert.All(measurements, m => Assert.Equal(
                new[] { CkMethodMetrics.MethodIdTag, CkMethodMetrics.StateTag }.Order(), m.Tags.Keys.Order()));
        }
    }

    /// <summary>Blocks <c>CreateAsync</c> until the test releases it, and notes whether it finished first.</summary>
    private sealed class GatedStore(ICkMethodOperationStore inner) : ICkMethodOperationStore
    {
        public TaskCompletionSource CreateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CreatedOperationId { get; private set; }
        public bool CreatedBeforeReturn { get; private set; }

        public async Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default)
        {
            CreateEntered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await inner.CreateAsync(record, cancellationToken);
            CreatedOperationId = record.OperationId;
            CreatedBeforeReturn = true;
        }

        public Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
            CancellationToken cancellationToken = default) => inner.GetAsync(tenantId, operationId, cancellationToken);

        public Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query,
            CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

        public Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.MarkRunningAsync(tenantId, operationId, nowUtc, cancellationToken);

        public Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
            DateTime nowUtc, CancellationToken cancellationToken = default) =>
            inner.ReportProgressAsync(tenantId, operationId, progress, nowUtc, cancellationToken);

        public Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(tenantId, operationId, nowUtc, cancellationToken);

        public Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
            CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(tenantId, operationId, completion, cancellationToken);

        public Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
            CancellationToken cancellationToken = default) =>
            inner.FailLostAsync(staleBeforeUtc, nowUtc, retention, cancellationToken);
    }

    private sealed class DownStore : ICkMethodOperationStore
    {
        private static CkMethodOperationStoreException Down() => new("database down");

        public Task CreateAsync(CkMethodOperationRecord record, CancellationToken cancellationToken = default) =>
            throw Down();

        public Task<CkMethodOperationRecord?> GetAsync(string tenantId, string operationId,
            CancellationToken cancellationToken = default) => throw Down();

        public Task<CkMethodOperationPage> ListAsync(CkMethodOperationQuery query,
            CancellationToken cancellationToken = default) => throw Down();

        public Task<bool> MarkRunningAsync(string tenantId, string operationId, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw Down();

        public Task<bool> ReportProgressAsync(string tenantId, string operationId, CkMethodOperationProgress progress,
            DateTime nowUtc, CancellationToken cancellationToken = default) => throw Down();

        public Task<bool> HeartbeatAsync(string tenantId, string operationId, DateTime nowUtc,
            CancellationToken cancellationToken = default) => throw Down();

        public Task<bool> CompleteAsync(string tenantId, string operationId, CkMethodOperationCompletion completion,
            CancellationToken cancellationToken = default) => throw Down();

        public Task<int> FailLostAsync(DateTime staleBeforeUtc, DateTime nowUtc, TimeSpan retention,
            CancellationToken cancellationToken = default) => throw Down();
    }
}

/// <summary>AB#8443: the setting <c>Methods:Operations:Retention</c> and its siblings are validated at start.</summary>
public class OperationOptionsTests
{
    private static string[] Failures(CkMethodOperationOptions options) =>
        new CkMethodOperationOptionsValidator().Validate(null, options).Failures?.ToArray() ?? [];

    [Fact]
    public void Defaults_AreValid_SevenDays()
    {
        Assert.Empty(Failures(new CkMethodOperationOptions()));
        Assert.Equal(TimeSpan.FromDays(7), new CkMethodOperationOptions().Retention);
    }

    [Theory]
    [InlineData(1.0 / 60)] // 1 minute
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31 * 24)]
    public void Retention_OutsideOneHourToThirtyDays_Fails_NamingTheSettingOnly(double hours)
    {
        var failures = Failures(new CkMethodOperationOptions { Retention = TimeSpan.FromHours(hours) });

        var failure = Assert.Single(failures);
        Assert.Contains("Methods:Operations:Retention", failure);
        Assert.DoesNotContain(TimeSpan.FromHours(hours).ToString(), failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30 * 24)]
    public void Retention_AtTheBounds_IsValid(int hours) =>
        Assert.Empty(Failures(new CkMethodOperationOptions { Retention = TimeSpan.FromHours(hours) }));

    [Fact]
    public void StaleAfter_BelowThreeHeartbeats_Fails()
    {
        var failures = Failures(new CkMethodOperationOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30), StaleAfter = TimeSpan.FromSeconds(60)
        });

        Assert.Contains(failures, f => f.Contains("Methods:Operations:StaleAfter"));
    }

    [Fact]
    public async Task InvalidRetention_FailsTheStartOfTheHost()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Methods:Operations:Retention"] = "00:10:00"
                }).Build());
        services.AddCkMethodInMemoryOperationStore();
        await using var provider = services.BuildServiceProvider();

        var e = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<CkMethodOperationOptions>>().Value);
        Assert.Contains("Methods:Operations:Retention", e.Message);
        Assert.DoesNotContain("00:10:00", e.Message);
    }
}
