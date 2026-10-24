using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Redaction;

/// <summary>AB#7007: filters see a redacted view of the response, the caller gets the real result.</summary>
public class RedactedResponseViewTests
{
    private const string Masked = "\"***\"";
    private const string Sentinel = "RESULT-SENTINEL-7f3a9c-Zq81";

    private static CkMethodDto Definition(bool sensitiveResult)
    {
        var definition = TestData.Definition();
        definition.Result!.Sensitive = sensitiveResult;
        return definition;
    }

    private static ChangePasswordHandler HandlerReturning(string value) =>
        new((_, _) => Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
            new UserChangePasswordResult { Value = value }));

    private static string ExpectedResult(string value) => JsonSerializer.Serialize(
        new UserChangePasswordResult { Value = value }, CkMethodJson.Options);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InProcess_FilterSeesMaskedResultOnlyForASensitiveResult_AndTheCallerGetsTheRealOne(bool sensitive)
    {
        var seen = new List<CkMethodResponse>();
        var audit = new ResponseFilter(CkMethodInvocationFilterOrder.Audit, seen);
        var host = InProcessFixture.With(HandlerReturning(Sentinel), Definition(sensitive), TestData.MethodId, audit);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal(ExpectedResult(Sentinel), response.ResultJson);
        var view = Assert.Single(seen);
        if (sensitive)
        {
            Assert.Equal(Masked, view.ResultJson);
            Assert.DoesNotContain(Sentinel, view.ResultJson);
        }
        else
        {
            Assert.Equal(ExpectedResult(Sentinel), view.ResultJson);
        }

        Assert.Equal(response.InvocationId, view.InvocationId);
        Assert.Equal(response.HandlerInstanceId, view.HandlerInstanceId);
        Assert.True(view.Success);
    }

    [Fact]
    public async Task UnknownMethod_MasksTheResultInTheFilterView_FailClosed_ButTheCallerStillGetsIt()
    {
        var seen = new List<CkMethodResponse>();
        var host = InProcessFixture.With(HandlerReturning(Sentinel), definition: null, TestData.MethodId,
            new ResponseFilter(CkMethodInvocationFilterOrder.Audit, seen));

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedResult(Sentinel), response.ResultJson);
        Assert.Equal(Masked, Assert.Single(seen).ResultJson);
    }

    [Fact]
    public void Resolver_IsFailClosed_ForNoSource_UnknownMethod_AndAFaultingSource()
    {
        Assert.True(new CkMethodSensitivityResolver(null).IsResultSensitive("t", "T/X", "T/X.M-1"));
        Assert.True(new CkMethodSensitivityResolver(new FuncSource(() => null)).IsResultSensitive("t", "T/X", "T/X.M-1"));
        Assert.True(new CkMethodSensitivityResolver(new FuncSource(() => throw new InvalidOperationException(Sentinel)))
            .IsResultSensitive("t", "T/X", "T/X.M-1"));
        Assert.True(new CkMethodSensitivityResolver(new FuncSource(() => Definition(true)))
            .IsResultSensitive("t", "T/X", "T/X.M-1"));
        Assert.False(new CkMethodSensitivityResolver(new FuncSource(() => Definition(false)))
            .IsResultSensitive("t", "T/X", "T/X.M-1"));
        var withoutResult = TestData.Definition(withResult: false);
        Assert.False(new CkMethodSensitivityResolver(new FuncSource(() => withoutResult))
            .IsResultSensitive("t", "T/X", "T/X.M-1"));
    }

    [Fact]
    public async Task FaultingDefinitionSource_MasksTheFilterView_AndTheCallerStillGetsTheResponse()
    {
        var seen = new List<CkMethodResponse>();
        var pipeline = Pipeline(new CkMethodSensitivityResolver(new FuncSource(() => throw new InvalidOperationException("boom"))),
            new ResponseFilter(CkMethodInvocationFilterOrder.Audit, seen));
        var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", $"{{\"value\":\"{Sentinel}\"}}");

        var response = await pipeline.ExecuteAsync(CkMethodTransportNames.Bus, TestData.Request(DateTime.UtcNow.AddSeconds(15)),
            _ => Task.FromResult(raw), TestContext.Current.CancellationToken);

        Assert.Equal(raw, response);
        Assert.Equal(Masked, Assert.Single(seen).ResultJson);
    }

    [Fact]
    public async Task PassingTheViewThrough_YieldsTheRawResponse_ToTheCaller()
    {
        var pipeline = Pipeline(Sensitive(true), new ResponseFilter(CkMethodInvocationFilterOrder.Audit, []),
            new ResponseFilter(CkMethodInvocationFilterOrder.Idempotency, []));
        var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", $"{{\"value\":\"{Sentinel}\"}}");

        var response = await pipeline.ExecuteAsync(CkMethodTransportNames.Bus, TestData.Request(DateTime.UtcNow.AddSeconds(15)),
            _ => Task.FromResult(raw), TestContext.Current.CancellationToken);

        Assert.Equal(raw, response);
    }

    [Fact]
    public async Task AFilterThatChangesTheResponse_KeepsItsChange()
    {
        var failed = CkMethodResponse.Failed(Guid.NewGuid(), "audit", CkMethodError.Create("AUDIT_REFUSED"));
        var other = CkMethodResponse.Succeeded(Guid.NewGuid(), "audit", "{\"value\":\"replaced\"}");
        var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", $"{{\"value\":\"{Sentinel}\"}}");

        foreach (var replacement in new[] { failed, other })
        {
            var pipeline = Pipeline(Sensitive(true),
                new ResponseFilter(CkMethodInvocationFilterOrder.Audit, []) { Rewrite = _ => replacement });

            var response = await pipeline.ExecuteAsync(CkMethodTransportNames.Bus,
                TestData.Request(DateTime.UtcNow.AddSeconds(15)), _ => Task.FromResult(raw),
                TestContext.Current.CancellationToken);

            Assert.Equal(replacement, response);
        }
    }

    [Fact]
    public async Task AFilterThatAnswersWithoutCallingNext_IsReturnedAsIs()
    {
        var refusal = CkMethodResponse.Failed(Guid.NewGuid(), string.Empty, CkMethodError.Create("METHOD_FORBIDDEN"));
        var transportCalls = 0;
        var pipeline = Pipeline(Sensitive(true), new ShortCircuitFilter(refusal));

        var response = await pipeline.ExecuteAsync(CkMethodTransportNames.Bus,
            TestData.Request(DateTime.UtcNow.AddSeconds(15)), _ =>
            {
                transportCalls++;
                return Task.FromResult(refusal);
            }, TestContext.Current.CancellationToken);

        Assert.Equal(refusal, response);
        Assert.Equal(0, transportCalls);
    }

    [Fact]
    public async Task ErrorAndAcceptedResponses_AreNotTouched()
    {
        var seen = new List<CkMethodResponse>();
        var pipeline = Pipeline(Sensitive(true), new ResponseFilter(CkMethodInvocationFilterOrder.Audit, seen));
        var failed = CkMethodResponse.Failed(Guid.NewGuid(), "h1", CkMethodError.Create("SOME_ERROR", ("a", "b")));
        var accepted = CkMethodResponse.Accepted(Guid.NewGuid(), "h1", new CkMethodOperationHandle { OperationId = "op-1" });

        foreach (var raw in new[] { failed, accepted })
        {
            var response = await pipeline.ExecuteAsync(CkMethodTransportNames.Bus,
                TestData.Request(DateTime.UtcNow.AddSeconds(15)), _ => Task.FromResult(raw),
                TestContext.Current.CancellationToken);
            Assert.Equal(raw, response);
        }

        Assert.Equal(new[] { failed, accepted }, seen);
    }

    [Fact]
    public void CreateViewAndRestore_AreMirrorImages()
    {
        var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", $"{{\"value\":\"{Sentinel}\"}}");

        var view = CkMethodResponseRedaction.CreateView(raw, resultSensitive: true);

        Assert.Equal(Masked, view.ResultJson);
        Assert.Equal(raw, CkMethodResponseRedaction.Restore(raw, view));
        Assert.Same(raw, CkMethodResponseRedaction.CreateView(raw, resultSensitive: false));
        Assert.Equal(view, CkMethodResponseRedaction.Restore(null, view)); // the transport did not run
        Assert.Null(CkMethodResponseRedaction.CreateView(CkMethodResponse.Succeeded(raw.InvocationId, "h1"), true)
            .ResultJson); // no result, nothing to mask
    }

    [Fact]
    public void ToString_NeverContainsTheResult_NotEvenThroughInterpolationOrCollections()
    {
        var response = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", $"{{\"value\":\"{Sentinel}\"}}");
        var failed = CkMethodResponse.Failed(response.InvocationId, "h1", CkMethodError.Create("SOME_ERROR"));

        Assert.DoesNotContain(Sentinel, response.ToString());
        Assert.DoesNotContain(Sentinel, $"{response}");
        Assert.DoesNotContain(Sentinel, string.Format("{0}", response));
        Assert.DoesNotContain(Sentinel, new CkMethodInvocationContext("bus", TestData.Request(DateTime.UtcNow), default).ToString());
        Assert.DoesNotContain(Sentinel, new List<CkMethodResponse> { response }.Aggregate("", (a, r) => a + r));
        Assert.DoesNotContain(Sentinel, (new { Response = response }).ToString());
        Assert.Contains(response.InvocationId.ToString(), response.ToString());
        Assert.Contains("SOME_ERROR", failed.ToString());
        Assert.Contains("success=True", response.ToString());
        // the wire shape is unchanged: the result is still serialized
        Assert.Contains(Sentinel, JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void WireEnvelope_IsUnchanged()
    {
        var raw = CkMethodResponse.Succeeded(Guid.Parse("11111111-1111-1111-1111-111111111111"), "h1", "{\"value\":\"x\"}");
        var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            "{\"contractVersion\":" + CkMethodContract.CurrentVersion +
            ",\"invocationId\":\"11111111-1111-1111-1111-111111111111\",\"success\":true,\"error\":null,\"resultJson\":\"{\\u0022value\\u0022:\\u0022x\\u0022}\",\"operation\":null,\"handlerInstanceId\":\"h1\"}",
            json);
    }

    // ---------------------------------------------------------------- bus transport through the dispatcher

    [Fact]
    public async Task Bus_FilterSeesMaskedResult_AndTheCallerGetsTheRealOne()
    {
        var seen = new List<CkMethodResponse>();
        var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", ExpectedResult(Sentinel));
        var logs = new FakeLogCollector();
        var dispatcher = Dispatcher(logs, Sensitive(true), raw, new ResponseFilter(CkMethodInvocationFilterOrder.Audit, seen));

        var response = await dispatcher.InvokeAsync(TestData.Request(DateTime.UtcNow.AddSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(raw, response);
        Assert.Equal(Masked, Assert.Single(seen).ResultJson);
        Assert.DoesNotContain(Sentinel, AllText(logs));
    }

    // ---------------------------------------------------------------- adversarial scan

    [Theory]
    [InlineData("in-process")]
    [InlineData("bus")]
    public async Task AdversarialScan_TheSentinelAppearsInNoLogNoTraceAndNoAuditRecord(string transport)
    {
        var activities = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => activities.Add(Describe(a))
        };
        ActivitySource.AddActivityListener(listener);
        var audit = new RecordingAudit();
        var logs = new FakeLogCollector();
        CkMethodResponse response;
        if (transport == "in-process")
        {
            var host = InProcessFixture.With(HandlerReturning(Sentinel), Definition(true), TestData.MethodId, audit);
            response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
                TestContext.Current.CancellationToken);
            logs = host.Logs;
        }
        else
        {
            var raw = CkMethodResponse.Succeeded(Guid.NewGuid(), "h1", ExpectedResult(Sentinel));
            response = await Dispatcher(logs, Sensitive(true), raw, audit).InvokeAsync(
                TestData.Request(DateTime.UtcNow.AddSeconds(15)), TestContext.Current.CancellationToken);
        }

        Assert.Contains(Sentinel, response.ResultJson); // the caller has it
        Assert.NotEmpty(audit.Records);
        Assert.All(audit.Records, r => Assert.DoesNotContain(Sentinel, r));
        Assert.DoesNotContain(Sentinel, AllText(logs));
        Assert.DoesNotContain(activities, a => a.Contains(Sentinel));
    }

    [Fact]
    public async Task HandlerWhoseResultSerializationThrowsWithTheValue_DoesNotLeakItToTheLog_ForASensitiveResult()
    {
        var logs = new FakeLogCollector();
        var audit = new RecordingAudit();
        var host = InProcessFixture.With(new LeakyHandler(Sentinel), Definition(true), TestData.MethodId, audit);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.DoesNotContain(Sentinel, host.AllLogText());
        Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(response));
        Assert.All(audit.Records, r => Assert.DoesNotContain(Sentinel, r));
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
        _ = logs;
    }

    [Fact]
    public async Task HandlerWhoseResultSerializationThrows_KeepsItsDiagnostics_ForANonSensitiveResult()
    {
        var host = InProcessFixture.With(new LeakyHandler("plain-value"), Definition(false), TestData.MethodId);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Contains("plain-value", host.AllLogText());
    }

    [Fact]
    public async Task BusHost_FailingResponseChannel_MasksTheSensitiveResultInTheLoggedException()
    {
        var host = HostFixture.With(HandlerReturning(Sentinel), Definition(true));

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)), TestDelivery.First,
            r => throw new InvalidOperationException($"cannot send {JsonSerializer.Serialize(r)} to the broker"));

        var log = host.AllLogText();
        Assert.DoesNotContain(Sentinel, log);
        Assert.Contains("***", log);
        Assert.Contains(host.Logs.GetSnapshot(), r => r.Level == LogLevel.Error);
    }

    [Fact]
    public async Task BusHost_FailingResponseChannel_LeavesANonSensitiveResultInTheLoggedException()
    {
        var host = HostFixture.With(HandlerReturning("plain-value"), Definition(false));

        await host.BusConsumer.CreateHandler()(TestData.Request(host.InSeconds(15)), TestDelivery.First,
            r => throw new InvalidOperationException($"cannot send {JsonSerializer.Serialize(r)} to the broker"));

        Assert.Contains("plain-value", host.AllLogText());
    }

    [Fact]
    public void Redactor_WithResult_MasksEveryValueOfTheResultInPlainAndEscapedForm()
    {
        var resultJson = "{\"value\":\"p\\\"w/ü<>\",\"n\":424242,\"nested\":{\"a\":[\"deep-secret\"]}}";
        var redactor = CkMethodRedactor.ForToken(TestData.Request(DateTime.UtcNow)).WithResult(resultJson);

        var text = redactor.Redact($"x {resultJson} y p\"w/ü<>  deep-secret 424242 {JsonSerializer.Serialize("p\"w/ü<>")}");

        Assert.DoesNotContain("deep-secret", text);
        Assert.DoesNotContain("424242", text);
        Assert.DoesNotContain("w/ü<>", text);
        Assert.DoesNotContain(resultJson, text);
        Assert.Same(redactor, redactor.WithResult(null));
    }

    // ---------------------------------------------------------------- helpers

    private static ICkMethodSensitivityResolver Sensitive(bool resultSensitive) =>
        new CkMethodSensitivityResolver(new FuncSource(() => Definition(resultSensitive)));

    private static ICkMethodInvocationPipeline Pipeline(ICkMethodSensitivityResolver resolver,
        params ICkMethodInvocationFilter[] filters) =>
        new CkMethodInvocationPipeline(filters, resolver,
            LoggerFactory.Create(_ => { }).CreateLogger<CkMethodInvocationPipeline>());

    private static CkMethodDispatcher Dispatcher(FakeLogCollector logs, ICkMethodSensitivityResolver resolver,
        CkMethodResponse busAnswer, params ICkMethodInvocationFilter[] filters)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var loggers = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(logs)));
        var hub = A.Fake<IDistributionEventHubService>();
        A.CallTo(() => hub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._, A<CkMethodRequest>._,
            A<CommandSendOptions>._, A<CancellationToken>._)).Returns(busAnswer);
        var resolverOfElement = A.Fake<ICkMethodDeclaringElementResolver>();
        A.CallTo(() => resolverOfElement.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
        var pipeline = new CkMethodInvocationPipeline(filters, resolver, loggers.CreateLogger<CkMethodInvocationPipeline>());
        return new CkMethodDispatcher(resolverOfElement, pipeline, new CkMethodHandlerRegistry(time), hub,
            new ServiceCollection().BuildServiceProvider(), resolver, Options.Create(new CkMethodDispatcherOptions()), time,
            loggers.CreateLogger<CkMethodDispatcher>());
    }

    private static string AllText(FakeLogCollector logs) =>
        string.Join("\n", logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception + "\n" +
                                                         string.Join(",", r.StructuredState?.Select(s => s.Key + "=" + s.Value) ?? [])));

    private static string Describe(Activity a) =>
        a.DisplayName + "|" + string.Join(",", a.TagObjects.Select(t => t.Key + "=" + t.Value)) + "|" +
        string.Join(",", a.Events.SelectMany(e => e.Tags.Select(t => t.Key + "=" + t.Value))) + "|" + a.StatusDescription;

    private sealed class FuncSource(Func<CkMethodDto?> find) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => find();
    }

    private class ResponseFilter(int order, List<CkMethodResponse> seen) : ICkMethodInvocationFilter
    {
        public Func<CkMethodResponse, CkMethodResponse>? Rewrite { get; init; }

        public int Order => order;

        public virtual async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context,
            CkMethodInvocationDelegate next)
        {
            var response = await next(context.Request);
            seen.Add(response);
            return Rewrite?.Invoke(response) ?? response;
        }
    }

    private sealed class ShortCircuitFilter(CkMethodResponse answer) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Authorization;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next) =>
            Task.FromResult(answer);
    }

    /// <summary>An audit stub: records what a real audit filter would store (request and response as text and JSON).</summary>
    private sealed class RecordingAudit : ICkMethodInvocationFilter
    {
        public List<string> Records { get; } = [];

        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            var response = await next(context.Request);
            Records.Add(context.Request + "|" + context.Request.ParametersJson + "|" + response + "|" +
                        JsonSerializer.Serialize(response) + "|" + response.ResultJson);
            return response;
        }
    }

    private sealed record LeakyResult
    {
        [JsonConverter(typeof(LeakyConverter))]
        public string? Value { get; init; }
    }

    private sealed class LeakyConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString()!;

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            throw new JsonException($"cannot write the value '{value}'");
    }

    private sealed class LeakyHandler(string value) : ICkMethodHandler<UserChangePasswordParameters, LeakyResult>
    {
        public Task<CkMethodOutcome<LeakyResult>> HandleAsync(CkMethodContext<UserChangePasswordParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<LeakyResult>>(new LeakyResult { Value = value });
    }
}
