using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>
///     OD-8 (AB#7022): an in-process handler runs detached from the ambient execution context of its caller, in a new
///     DI scope; trace, culture, logging scope and cancellation are carried explicitly; a registered probe is a tripwire.
/// </summary>
public class InProcessIsolationTests
{
    private static readonly AsyncLocal<string?> RequestAmbient = new();

    private sealed class ScopedMarker;

    private sealed class FixedProbe(bool visible) : ICkInProcessAmbientStateProbe
    {
        public int Calls;
        public IServiceProvider? Services;

        public bool IsRequestStateVisible(IServiceProvider handlerServices)
        {
            Interlocked.Increment(ref Calls);
            Services = handlerServices;
            return visible;
        }
    }

    /// <summary>The probe a host would write: reads an ambient value of the current flow.</summary>
    private sealed class AmbientValueProbe : ICkInProcessAmbientStateProbe
    {
        public bool IsRequestStateVisible(IServiceProvider handlerServices) => RequestAmbient.Value != null;
    }

    private sealed class ThrowingProbe : ICkInProcessAmbientStateProbe
    {
        public bool IsRequestStateVisible(IServiceProvider handlerServices) =>
            throw new InvalidOperationException("secret-session-data");
    }

    /// <summary>Handler that records what it sees. Constructed per scope, like a real handler.</summary>
    private sealed class ObservingHandler(ILoggerFactory loggers, ScopedMarker marker)
        : ICkMethodHandler<UserChangePasswordParameters, UserChangePasswordResult>
    {
        public int Calls;
        public string? AmbientAtEntry = "<unset>";
        public string? AmbientAfterAwait = "<unset>";
        public ScopedMarker Marker { get; } = marker;
        public Activity? ActivityAtEntry;
        public CultureInfo? Culture;
        public CultureInfo? UiCulture;
        public CancellationToken Token;

        public async Task<CkMethodOutcome<UserChangePasswordResult>> HandleAsync(
            CkMethodContext<UserChangePasswordParameters> context, CancellationToken cancellationToken)
        {
            Calls++;
            AmbientAtEntry = RequestAmbient.Value;
            ActivityAtEntry = Activity.Current;
            Culture = CultureInfo.CurrentCulture;
            UiCulture = CultureInfo.CurrentUICulture;
            Token = cancellationToken;
            loggers.CreateLogger("handler").LogInformation("inside the handler");
            await Task.Yield();
            AmbientAfterAwait = RequestAmbient.Value;
            RequestAmbient.Value = "set-by-handler"; // must never leak back to the caller
            return new UserChangePasswordResult { Value = "ok" };
        }
    }

    private static (InProcessFixture Fixture, Func<ObservingHandler> Handler) Build(
        params ICkInProcessAmbientStateProbe[] probes)
    {
        ObservingHandler? last = null;
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        services.AddScoped(sp =>
        {
            last = new ObservingHandler(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<ScopedMarker>());
            return last;
        });
        var fixture = InProcessFixture.BuildWithProbes<ObservingHandler>(services, probes);
        return (fixture, () => last ?? throw new InvalidOperationException("handler was not created"));
    }

    private static CkMethodRequest Request(InProcessFixture f) => TestData.Request(f.InSeconds(30));

    [Fact]
    public async Task AsyncLocalOfTheCaller_IsEmptyInTheHandler_AlsoAfterItsFirstAwait_AndNothingLeaksBack()
    {
        var (fixture, handler) = Build();
        RequestAmbient.Value = "request-session";
        try
        {
            var response = await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

            Assert.True(response.Success);
            Assert.Null(handler().AmbientAtEntry);
            Assert.Null(handler().AmbientAfterAwait);
            Assert.Equal("request-session", RequestAmbient.Value); // the caller's flow is untouched
        }
        finally
        {
            RequestAmbient.Value = null;
        }
    }

    [Fact]
    public async Task ScopedServices_ComeFromANewScopePerInvocation()
    {
        var (fixture, handler) = Build();
        using var callerScope = fixture.Provider.CreateScope();
        var callerMarker = callerScope.ServiceProvider.GetRequiredService<ScopedMarker>();

        await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);
        var first = handler().Marker;
        await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);
        var second = handler().Marker;

        Assert.NotSame(callerMarker, first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task TheCallersActivity_IsTheParentOfTheHandlerSpan()
    {
        using var callerSource = new ActivitySource("test.caller");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "test.caller" or CkMethodTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        var (fixture, handler) = Build();
        using var caller = callerSource.StartActivity("caller");
        Assert.NotNull(caller);

        await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

        var span = handler().ActivityAtEntry;
        Assert.NotNull(span);
        Assert.Equal(CkMethodTelemetry.ActivitySourceName, span.Source.Name);
        Assert.Equal(caller.TraceId, span.TraceId);
        Assert.Equal(caller.SpanId, span.ParentSpanId);
        Assert.Equal(TestData.MethodId, span.GetTagItem("ck.method.id"));
        Assert.Equal(CkMethodTransportNames.InProcess, span.GetTagItem("ck.method.transport"));
        Assert.Same(caller, Activity.Current); // the handler's activity did not replace the caller's
    }

    [Fact]
    public async Task WithoutAListenerOfOurSource_TheCallersTraceStillContinuesInTheHandler()
    {
        using var callerSource = new ActivitySource("test.caller.only");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.caller.only",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        var (fixture, handler) = Build();
        using var caller = callerSource.StartActivity("caller");
        Assert.NotNull(caller);

        await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

        Assert.Equal(caller.TraceId, handler().ActivityAtEntry?.TraceId);
    }

    [Fact]
    public async Task CultureAndUiCulture_AreCarriedIntoTheHandler()
    {
        var (fixture, handler) = Build();
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = new CultureInfo("de-AT");
        CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
        try
        {
            await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

            Assert.Equal("de-AT", handler().Culture?.Name);
            Assert.Equal("fr-FR", handler().UiCulture?.Name);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before;
        }
    }

    [Fact]
    public async Task OnlyTheCallIdentifyingLoggingScope_IsVisibleInTheHandler_NotTheCallersScopes()
    {
        var (fixture, _) = Build();
        var request = Request(fixture);
        using var callerScope = fixture.Loggers.CreateLogger("caller")
            .BeginScope(new Dictionary<string, object> { ["RequestPath"] = "/graphql" });

        await fixture.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        var record = fixture.Logs.GetSnapshot().Single(r => r.Message == "inside the handler");
        var scope = Assert.Single(record.Scopes!.OfType<IReadOnlyDictionary<string, object>>());
        Assert.Equal(TestData.MethodId, scope["MethodId"]);
        Assert.Equal(request.InvocationId, scope["InvocationId"]);
        Assert.Equal(request.CorrelationId ?? request.InvocationId, scope["CorrelationId"]);
        Assert.Equal(request.TenantId, scope["TenantId"]);
        Assert.DoesNotContain(record.Scopes!.OfType<IReadOnlyDictionary<string, object>>(),
            s => s.ContainsKey("RequestPath"));
    }

    [Fact]
    public async Task TheCallersCancellationToken_ReachesTheHandler()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped(_ => new ChangePasswordHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new UserChangePasswordResult();
        }));
        var fixture = InProcessFixture.Build<ChangePasswordHandler>(services);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var call = fixture.Transport.InvokeAsync(Request(fixture), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task TheDeadline_CancelsTheHandlerToken_AndTheCallerGetsATimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ChangePasswordHandler(async (_, token) =>
        {
            seen = token;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new UserChangePasswordResult();
        }));
        var fixture = InProcessFixture.Build<ChangePasswordHandler>(services);

        var call = fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.Time.Advance(TimeSpan.FromSeconds(31));
        var response = await call;

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.True(seen.IsCancellationRequested);
    }

    // ---- tripwire ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProbeReportingState_FailsTheCall_BeforeTheHandlerIsCreated_AndTheLogNamesTheProbeOnly()
    {
        var probe = new FixedProbe(true);
        var (fixture, handler) = Build(probe);
        var request = Request(fixture);

        var response = await fixture.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
        Assert.Equal(1, probe.Calls);
        Assert.NotNull(probe.Services);
        Assert.Throws<InvalidOperationException>(() => handler()); // the handler was never created
        Assert.DoesNotContain(fixture.Logs.GetSnapshot(), r => r.Message == "inside the handler");
        var log = fixture.AllLogText();
        Assert.Contains(typeof(FixedProbe).FullName!, log);
        Assert.DoesNotContain(TestData.Secret, log);
        Assert.DoesNotContain(TestData.Token, log);
    }

    [Fact]
    public async Task ProbeReadingTheAmbientValue_SeesItEmpty_BecauseTheHandlerFlowIsDetached()
    {
        var (fixture, handler) = Build(new AmbientValueProbe());
        RequestAmbient.Value = "request-session";
        try
        {
            var response = await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

            Assert.True(response.Success);
            Assert.Equal(1, handler().Calls);
        }
        finally
        {
            RequestAmbient.Value = null;
        }
    }

    [Fact]
    public async Task ProbeReportingNothing_LetsTheCallThrough()
    {
        var probe = new FixedProbe(false);
        var (fixture, handler) = Build(probe);

        var response = await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, handler().Calls);
    }

    [Fact]
    public async Task ThrowingProbe_FailsClosed_AndNeverLogsTheExceptionMessage()
    {
        var (fixture, _) = Build(new ThrowingProbe());

        var response = await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var log = fixture.AllLogText();
        Assert.Contains(typeof(ThrowingProbe).FullName!, log);
        Assert.DoesNotContain("secret-session-data", log);
    }

    [Fact]
    public async Task AllProbesAreAsked_TheFirstReportingOneFailsTheCall()
    {
        var quiet = new FixedProbe(false);
        var loud = new FixedProbe(true);
        var (fixture, _) = Build(quiet, loud);

        var response = await fixture.Transport.InvokeAsync(Request(fixture), TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(1, quiet.Calls);
        Assert.Equal(1, loud.Calls);
    }

    [Fact]
    public void ProbeRegistration_AddsAProbeToTheContainer()
    {
        var services = new ServiceCollection();

        services.AddCkInProcessAmbientStateProbe<AmbientValueProbe>();

        Assert.Single(services.BuildServiceProvider().GetServices<ICkInProcessAmbientStateProbe>());
    }
}
