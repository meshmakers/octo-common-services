using System.Diagnostics;
using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>
///     The in-process transport must answer exactly what the bus path answers: both run the same invoker, so every
///     scenario is compared envelope against envelope.
/// </summary>
public class InProcessTransportTests
{
    private const string Declared = "PASSWORD_POLICY_VIOLATION";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private static string Normalized(CkMethodResponse response) =>
        JsonSerializer.Serialize(response with { HandlerInstanceId = "<instance>" }, Wire);

    private static ChangePasswordHandler Handler(
        Func<CkMethodContext<UserChangePasswordParameters>, CancellationToken, Task<CkMethodOutcome<UserChangePasswordResult>>>? run = null) =>
        new(run);

    /// <summary>Runs one scenario on both transports and returns both envelopes.</summary>
    private static async Task<(CkMethodResponse Bus, CkMethodResponse InProcess)> RunBoth(
        Func<ChangePasswordHandler> handler, Func<CkMethodRequest, CkMethodRequest>? shape = null,
        Func<FakeTime, Task>? waitFor = null)
    {
        var busHost = HostFixture.With(handler(), TestData.Definition(errors: Declared));
        var inProcess = InProcessFixture.With(handler(), TestData.Definition(errors: Declared));
        Assert.Equal(busHost.Time.GetUtcNow(), inProcess.Time.GetUtcNow());
        var request = TestData.Request(busHost.InSeconds(15));
        if (shape != null)
        {
            request = shape(request);
        }

        var bus = busHost.BusConsumer.HandleAsync(request);
        var local = inProcess.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
        if (waitFor != null)
        {
            await Task.Yield();
            await waitFor(new FakeTime(busHost.Time, inProcess.Time));
        }

        return (await bus, await local);
    }

    private sealed record FakeTime(Microsoft.Extensions.Time.Testing.FakeTimeProvider Bus,
        Microsoft.Extensions.Time.Testing.FakeTimeProvider InProcess)
    {
        public void Advance(TimeSpan by)
        {
            Bus.Advance(by);
            InProcess.Advance(by);
        }
    }

    [Fact]
    public async Task Success_ResultJsonAndEnvelope_EqualTheBusPath()
    {
        var (bus, local) = await RunBoth(() => Handler());

        Assert.True(local.Success);
        Assert.Equal("{\"value\":\"changed\"}", local.ResultJson);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task DeclaredError_EqualsTheBusPath()
    {
        var (bus, local) = await RunBoth(() => Handler((_, _) =>
            Task.FromResult(CkMethodOutcome<UserChangePasswordResult>.Fail(Declared, ("minLength", "12")))));

        Assert.Equal(Declared, local.Error!.Code);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task ThrownCkMethodException_EqualsTheBusPath()
    {
        var (bus, local) = await RunBoth(() =>
            Handler((_, _) => throw new CkMethodException(Declared, ("minLength", "12"))));

        Assert.Equal(Declared, local.Error!.Code);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task UnexpectedException_IsHandlerFailedWithTheCorrelationId_EqualsTheBusPath()
    {
        var correlation = Guid.NewGuid();
        var (bus, local) = await RunBoth(
            () => Handler((_, _) => throw new InvalidOperationException("db is down")),
            r => r with { CorrelationId = correlation });

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, local.Error!.Code);
        Assert.Equal(correlation.ToString(), local.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task DeadlineAlreadyPassed_IsTimeoutWithoutRunningTheHandler_EqualsTheBusPath()
    {
        var handlers = new List<ChangePasswordHandler>();
        var (bus, local) = await RunBoth(() =>
        {
            var h = Handler();
            handlers.Add(h);
            return h;
        }, r => r with { DeadlineUtc = r.DeadlineUtc.AddSeconds(-30) });

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, local.Error!.Code);
        Assert.All(handlers, h => Assert.Equal(0, h.Calls));
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task CooperativeHandlerAtItsDeadline_IsTimeoutWithTimeoutSeconds_EqualsTheBusPath()
    {
        var (bus, local) = await RunBoth(
            () => Handler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new UserChangePasswordResult();
            }),
            waitFor: async time =>
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                time.Advance(TimeSpan.FromSeconds(16));
            });

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, local.Error!.Code);
        Assert.Equal(TestData.MethodId, local.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Contains(CkMethodErrorParameters.TimeoutSeconds, local.Error.Parameters.Keys);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task HandlerThatIgnoresItsToken_StillAnswersTimeoutAtTheDeadline()
    {
        var never = new TaskCompletionSource<CkMethodOutcome<UserChangePasswordResult>>();
        var host = InProcessFixture.With(
            Handler((_, _) => never.Task), TestData.Definition(errors: Declared));
        var call = host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);
        host.Time.Advance(TimeSpan.FromSeconds(16));
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Contains(CkMethodErrorParameters.TimeoutSeconds, response.Error.Parameters.Keys);
        never.SetResult(new UserChangePasswordResult()); // the late result is dropped, nothing throws
    }

    [Fact]
    public async Task UnsupportedContractVersion_EqualsTheBusPath()
    {
        var (bus, local) = await RunBoth(() => Handler(),
            r => r with { ContractVersion = CkMethodContract.CurrentVersion + 1 });

        Assert.Equal(CkMethodErrorCodes.MethodContractVersionUnsupported, local.Error!.Code);
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task UnknownMethod_IsHandlerNotAvailable_Immediately_EqualsTheBusPath()
    {
        var busHost = HostFixture.With(Handler(), TestData.Definition());
        var inProcess = InProcessFixture.With(Handler(), TestData.Definition());
        var request = TestData.Request(inProcess.InSeconds(15)) with { MethodId = "System.Identity/User.Unknown-1" };

        var stopwatch = Stopwatch.StartNew();
        var local = await inProcess.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
        stopwatch.Stop();
        var bus = await busHost.BusConsumer.HandleAsync(request);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, local.Error!.Code);
        Assert.False(inProcess.Transport.CanHandle(request.MethodId));
        Assert.True(inProcess.Transport.CanHandle(TestData.MethodId));
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task InvalidParameters_AreValidationFailed_AndNeverEchoTheValue_EqualsTheBusPath()
    {
        var (bus, local) = await RunBoth(() => Handler(),
            r => r with { ParametersJson = "{\"currentPassword\":\"" + TestData.OldSecret + "\"}" });

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, local.Error!.Code);
        Assert.Equal("newPassword", local.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.DoesNotContain(TestData.OldSecret, Normalized(local));
        Assert.Equal(Normalized(bus), Normalized(local));
    }

    [Fact]
    public async Task CancellationByTheCaller_SurfacesAsOperationCanceled()
    {
        var host = InProcessFixture.With(Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new UserChangePasswordResult();
        }), TestData.Definition());
        using var cts = new CancellationTokenSource();
        var call = host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), cts.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task CallerIdentityAndToken_ReachTheHandler_AndNeverTheLogs()
    {
        var handler = Handler((_, _) => throw new InvalidOperationException(
            $"update failed for {TestData.Secret} with token {TestData.Token}"));
        var host = InProcessFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(TestData.Token, handler.LastContext!.CallerAccessToken);
        Assert.Equal("sub-1", handler.LastContext.Caller!.SubjectId);
        var text = host.AllLogText();
        Assert.NotEmpty(host.Logs.GetSnapshot());
        Assert.DoesNotContain(TestData.Token, text);
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, Normalized(response));
    }

    [Fact]
    public async Task ScopedHandlerDependencies_GetAFreshScopePerCall_NotTheCallersScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeProbe>();
        services.AddScoped<ProbeHandler>();
        var host = InProcessFixture.Build<ProbeHandler>(services, null, TestData.MethodId);
        var callerScope = new ServiceCollection().AddScoped<ScopeProbe>().BuildServiceProvider().CreateScope();
        var callerProbe = callerScope.ServiceProvider.GetRequiredService<ScopeProbe>();

        var first = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);
        var second = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.ResultJson, second.ResultJson);
        Assert.DoesNotContain(callerProbe.Id.ToString(), first.ResultJson);
    }

    internal sealed class ScopeProbe
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    internal sealed class ProbeHandler(ScopeProbe probe) : ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>
    {
        public Task<CkMethodOutcome<UserChangePasswordResult>> HandleAsync(
            CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
                new UserChangePasswordResult { Value = probe.Id.ToString() });
    }
}
