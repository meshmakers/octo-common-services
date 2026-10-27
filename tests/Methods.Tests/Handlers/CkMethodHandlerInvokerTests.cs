using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

public class CkMethodHandlerInvokerTests
{
    private static Task<CkMethodResponse> Invoke(HostFixture host, CkMethodRequest request,
        CancellationToken? cancellationToken = null) =>
        host.Invoker.InvokeAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);

    private static ChangePasswordHandler Handler(
        Func<CkMethodContext<UserChangePasswordParameters>, CancellationToken, Task<CkMethodOutcome<UserChangePasswordResult>>>? run = null) =>
        new(run);

    [Fact]
    public async Task HappyPath_DeserializesParameters_AndReturnsResultInEnvelope()
    {
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var response = await Invoke(host, request);

        Assert.True(response.Success);
        Assert.Null(response.Error);
        Assert.Equal("{\"value\":\"changed\"}", response.ResultJson);
        Assert.Equal(request.InvocationId, response.InvocationId);
        Assert.Equal(host.Instance.InstanceId, response.HandlerInstanceId);
        Assert.Equal(TestData.Secret, handler.LastContext!.Parameters.NewPassword);
        Assert.Equal(TestData.OldSecret, handler.LastContext.Parameters.CurrentPassword);
        Assert.Null(handler.LastContext.Parameters.Hint);
        Assert.Equal("meshtest", handler.LastContext.TenantId);
        Assert.Equal("65f0c0ffee", handler.LastContext.TargetRtId);
        Assert.Equal("sub-1", handler.LastContext.Caller!.SubjectId);
    }

    [Fact]
    public async Task HandlerWithoutResult_Succeeds_WithoutResultJson()
    {
        var host = HostFixture.With(new NoResultHandler(), TestData.Definition(withResult: false));

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.True(response.Success);
        Assert.Null(response.ResultJson);
        Assert.Null(response.Operation);
    }

    [Fact]
    public async Task DeclaredError_IsMappedToCodeAndParameters_WithoutMessageText()
    {
        var handler = Handler((_, _) => Task.FromResult(CkMethodOutcome<UserChangePasswordResult>.Fail(
            "PASSWORD_POLICY_VIOLATION", ("minLength", "12"))));
        var host = HostFixture.With(handler, TestData.Definition(errors: "PASSWORD_POLICY_VIOLATION"));

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.False(response.Success);
        Assert.Equal("PASSWORD_POLICY_VIOLATION", response.Error!.Code);
        Assert.Equal("12", response.Error.Parameters["minLength"]);
        Assert.Null(response.ResultJson);
    }

    [Fact]
    public async Task ThrownCkMethodException_IsMappedLikeAReturnedError()
    {
        var handler = Handler((_, _) => throw new CkMethodException("PASSWORD_POLICY_VIOLATION", ("minLength", "12")));
        var host = HostFixture.With(handler, TestData.Definition(errors: "PASSWORD_POLICY_VIOLATION"));

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.Equal("PASSWORD_POLICY_VIOLATION", response.Error!.Code);
        Assert.Equal("12", response.Error.Parameters["minLength"]);
    }

    [Fact]
    public async Task PlatformErrorHelpers_CarryTheMethodIdAndTheirParameters()
    {
        var handler = Handler((_, _) =>
            Task.FromResult(CkMethodOutcome<UserChangePasswordResult>.ValidationFailed("newPassword", "too-short")));
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("newPassword", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal("too-short", response.Error.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(TestData.MethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public async Task UndeclaredErrorCode_IsAHandlerBug_AnsweredAsHandlerFailed()
    {
        var handler = Handler((_, _) => Task.FromResult(CkMethodOutcome<UserChangePasswordResult>.Fail("NOT_DECLARED")));
        var host = HostFixture.With(handler, TestData.Definition(errors: "PASSWORD_POLICY_VIOLATION"));

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error && r.Message.Contains("NOT_DECLARED"));
    }

    [Fact]
    public async Task ThrowingHandler_AnswersHandlerFailedWithCorrelationId_AndLogsTheExceptionWithThatId()
    {
        var correlation = Guid.NewGuid();
        var handler = Handler((_, _) => throw new InvalidOperationException("db is down"));
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15)) with { CorrelationId = correlation };

        var response = await Invoke(host, request);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Equal(correlation.ToString(), response.Error.Parameters[CkMethodErrorParameters.CorrelationId]);
        // nothing of the exception reaches the wire
        Assert.DoesNotContain("db is down", System.Text.Json.JsonSerializer.Serialize(response));
        Assert.Null(response.Error.Details);
        var record = Assert.Single(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
        Assert.Contains(correlation.ToString(), record.Message);
        Assert.Contains("db is down", record.Exception!.Message);
    }

    [Fact]
    public async Task ThrowingHandler_WithoutCorrelationId_UsesTheInvocationId()
    {
        var host = HostFixture.With(Handler((_, _) => throw new InvalidOperationException("x")), TestData.Definition());
        var request = TestData.Request(host.InSeconds(15));

        var response = await Invoke(host, request);

        Assert.Equal(request.InvocationId.ToString(), response.Error!.Parameters[CkMethodErrorParameters.CorrelationId]);
    }

    [Fact]
    public async Task DeadlineAlreadyPassed_AnswersTimeout_WithoutExecuting()
    {
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(-1)));

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal("15", response.Error.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task HandlerRunningPastTheDeadline_IsCancelled_AndAnsweredAsTimeout()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        var handler = Handler(async (_, ct) =>
        {
            seen = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new UserChangePasswordResult();
        });
        var host = HostFixture.With(handler, TestData.Definition());

        var invocation = Invoke(host, TestData.Request(host.InSeconds(5)));
        await started.Task;
        Assert.False(seen.IsCancellationRequested);
        host.Time.Advance(TimeSpan.FromSeconds(6));
        var response = await invocation;

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
    }

    [Fact]
    public async Task CallerCancellation_SurfacesAsOperationCanceled_NotAsAnAnswer()
    {
        var started = new TaskCompletionSource();
        var handler = Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new UserChangePasswordResult();
        });
        var host = HostFixture.With(handler, TestData.Definition());
        using var cts = new CancellationTokenSource();

        var invocation = Invoke(host, TestData.Request(host.InSeconds(60)), cts.Token);
        await started.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
    }

    [Fact]
    public async Task RemainingTime_ReflectsTheDeadline()
    {
        TimeSpan? remaining = null;
        var handler = Handler((ctx, _) =>
        {
            remaining = ctx.RemainingTime;
            return Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult());
        });
        var host = HostFixture.With(handler);

        await Invoke(host, TestData.Request(host.InSeconds(15)));

        Assert.Equal(TimeSpan.FromSeconds(15), remaining);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task NewerContractVersion_IsAnsweredAsUnsupported_WithoutExecuting(int version)
    {
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(15), contractVersion: version));

        Assert.Equal(CkMethodErrorCodes.MethodContractVersionUnsupported, response.Error!.Code);
        Assert.Equal(version.ToString(), response.Error.Parameters[CkMethodErrorParameters.RequestedVersion]);
        Assert.Equal("1", response.Error.Parameters[CkMethodErrorParameters.SupportedVersion]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnregisteredMethod_IsAnsweredAsHandlerNotAvailable()
    {
        var host = HostFixture.With(Handler(), TestData.Definition());
        var request = TestData.Request(host.InSeconds(15)) with { MethodId = "System.Identity/User.Other-1" };

        var response = await Invoke(host, request);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.False(host.Invoker.CanHandle(request.MethodId));
        Assert.True(host.Invoker.CanHandle(TestData.MethodId));
    }

    [Fact]
    public async Task MissingRequiredParameter_IsValidationFailed_NamingTheField()
    {
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());
        var request = TestData.Request(host.InSeconds(15), parametersJson: "{\"currentPassword\":\"x\"}");

        var response = await Invoke(host, request);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("newPassword", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task WrongParameterType_IsValidationFailed_WithoutEchoingTheValue()
    {
        var host = HostFixture.With(Handler(), TestData.Definition());
        var request = TestData.Request(host.InSeconds(15),
            parametersJson: "{\"currentPassword\":{\"leak\":\"Z3cret\"},\"newPassword\":\"x\"}");

        var response = await Invoke(host, request);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("currentPassword", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.DoesNotContain("Z3cret", System.Text.Json.JsonSerializer.Serialize(response));
        Assert.DoesNotContain("Z3cret", host.AllLogText());
    }

    [Fact]
    public async Task NotJson_IsValidationFailed()
    {
        var host = HostFixture.With(Handler(), TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(15), parametersJson: "not json"));

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
    }

    [Fact]
    public async Task SensitiveParameters_NeverAppearInLogs_EvenWhenAHandlerPutsThemIntoAnException()
    {
        var handler = Handler((ctx, _) => throw new InvalidOperationException(
            $"cannot set {ctx.Parameters.NewPassword} (was {ctx.Parameters.CurrentPassword}), token {ctx.CallerAccessToken}"));
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(15)));

        var logs = host.AllLogText();
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.NotEmpty(host.Logs.GetSnapshot());
        Assert.DoesNotContain(TestData.Secret, logs);
        Assert.DoesNotContain(TestData.OldSecret, logs);
        Assert.DoesNotContain(TestData.Token, logs);
        Assert.Contains(CkMethodRedactor.Mask, logs);
        Assert.DoesNotContain(TestData.Secret, System.Text.Json.JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task EveryLogLine_OfEveryOutcome_IsFreeOfSensitiveValues()
    {
        var host = HostFixture.With(Handler(), TestData.Definition());
        await Invoke(host, TestData.Request(host.InSeconds(15)));
        await Invoke(host, TestData.Request(host.InSeconds(-5)));
        await Invoke(host, TestData.Request(host.InSeconds(15), contractVersion: 9));
        await Invoke(host, TestData.Request(host.InSeconds(15)) with { MethodId = "A.B/C.D-1" });

        var logs = host.AllLogText() + string.Join("", host.Logs.GetSnapshot().Select(r => r.StructuredState));
        Assert.DoesNotContain(TestData.Secret, logs);
        Assert.DoesNotContain(TestData.Token, logs);
    }

    [Fact]
    public async Task Handler_IsResolvedInANewScopePerInvocation()
    {
        var scopes = new List<object>();
        var services = new ServiceCollection();
        services.AddScoped<ScopeMarker>();
        services.AddScoped<ScopedHandler>(sp =>
        {
            scopes.Add(sp.GetRequiredService<ScopeMarker>());
            return new ScopedHandler();
        });
        var provider = services.BuildServiceProvider();
        var host = HostFixture.With(new ScopedHandler());
        var invoker = new CkMethodHandlerInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            host.Registrations, host.Instance, host.Time,
            new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodHandlerInvoker>(),
            new CkMethodAuthorizationEvaluator(Microsoft.Extensions.Options.Options.Create(new CkMethodAuthorizationOptions())),
            tokenValidator: new FixedPrincipalValidator(HostFixture.Administrator));

        await invoker.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);
        await invoker.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(2, scopes.Count);
        Assert.NotSame(scopes[0], scopes[1]);
    }

    private sealed class ScopeMarker;

    private sealed class ScopedHandler : ICkMethodHandler<UserChangePasswordParameters>
    {
        public Task<CkMethodOutcome> HandleAsync(CkMethodContext<UserChangePasswordParameters> context,
            CancellationToken cancellationToken) => Task.FromResult(CkMethodOutcome.Ok());
    }

    [Fact]
    public async Task TimeoutAnswer_CarriesTheTimeoutOfTheRequest_NotTheDefaultOfTheHost()
    {
        // the definition has no execution.timeoutSeconds (default 15), the request says 2
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(-1)) with { TimeoutSeconds = 2 });

        Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
        Assert.Equal("2", response.Error.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TimeoutAtTheDeadlineOfARunningHandler_CarriesTheTimeoutOfTheRequest()
    {
        var started = new TaskCompletionSource();
        var handler = Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new UserChangePasswordResult();
        });
        var host = HostFixture.With(handler, TestData.Definition());

        var invocation = Invoke(host, TestData.Request(host.InSeconds(2)) with { TimeoutSeconds = 2 });
        await started.Task;
        host.Time.Advance(TimeSpan.FromSeconds(3));
        var response = await invocation;

        Assert.Equal("2", response.Error!.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
    }

    [Fact]
    public async Task TimeoutAnswer_WithoutTimeoutInTheRequest_FallsBackToTheDefinition()
    {
        var host = HostFixture.With(Handler(), TestData.Definition());

        var response = await Invoke(host, TestData.Request(host.InSeconds(-1)));

        Assert.Equal("15", response.Error!.Parameters[CkMethodErrorParameters.TimeoutSeconds]);
    }

    [Fact]
    public async Task DeadlineMaxValue_DoesNotThrow_TheHandlerRuns_AndItsTokenIsNotFiredEarly()
    {
        CancellationToken seen = default;
        var handler = Handler((_, ct) =>
        {
            seen = ct;
            return Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult());
        });
        var host = HostFixture.With(handler, TestData.Definition());

        var response = await Invoke(host, TestData.Request(DateTime.MaxValue));

        Assert.True(response.Success);
        Assert.False(seen.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtremeDeadlines_NeverThrow_TheyAreTimeoutOrServed(bool minValue)
    {
        var handler = Handler();
        var host = HostFixture.With(handler, TestData.Definition());
        var deadline = minValue
            ? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
            : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        var response = await Invoke(host, TestData.Request(deadline));

        if (minValue)
        {
            Assert.Equal(CkMethodErrorCodes.MethodTimeout, response.Error!.Code);
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            Assert.True(response.Success);
        }
    }
}
