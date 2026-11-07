using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Dispatching;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Metrics;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.InProcess;

/// <summary>
///     AB#8411: a service calls a CK method it hosts itself (a REST facade) through <see cref="ICkMethodDispatcher" />. The
///     handler runs in this process through the same invocation core as on the bus, whether the method is offered on the bus
///     (<c>Handle</c>) or kept local only (<c>HandleLocalOnly</c>), and nothing is sent to the broker.
/// </summary>
public class SelfHostedMethodCallTests
{
    private const string LocalOnlyMethodId = "System.Identity/User.ResetPassword-1";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly CkMethodPrincipal Stranger =
        new("fedcba9876543210fedcba98", ["SomeoneElse"], [CkMethodAuthorizationOptions.ApiScope]);

    // ---------------------------------------------------------------- host

    private sealed class RecordingAuditRecorder : ICkMethodAuditRecorder
    {
        public List<CkMethodAuditRecord> Records { get; } = [];

        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            lock (Records)
            {
                Records.Add(record);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Records the transport and the request (a redacted view) every pipeline run saw.</summary>
    private sealed class RecordingFilter : ICkMethodInvocationFilter
    {
        public List<CkMethodInvocationContext> Contexts { get; } = [];

        public int Order => CkMethodInvocationFilterOrder.Authorization + 1;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            lock (Contexts)
            {
                Contexts.Add(context);
            }

            return next(context.Request);
        }
    }

    /// <summary>The real evaluator, recording the principal it was asked about.</summary>
    private sealed class SpyEvaluator : ICkMethodAuthorizationEvaluator
    {
        private readonly CkMethodAuthorizationEvaluator _inner = new(Options.Create(new CkMethodAuthorizationOptions()));

        public List<CkMethodPrincipal?> Principals { get; } = [];

        public CkMethodAuthorizationDecision Evaluate(CkMethodAuthorizationDto? declaration, CkMethodKindDto kind,
            string? targetRtId, CkMethodPrincipal? principal)
        {
            lock (Principals)
            {
                Principals.Add(principal);
            }

            return _inner.Evaluate(declaration, kind, targetRtId, principal);
        }
    }

    /// <summary>One service that hosts handlers (bus and/or local only) and has the dispatcher, with a faked event hub.</summary>
    private sealed class SelfHost
    {
        public required ServiceProvider Provider { get; init; }

        public required IDistributionEventHubService EventHub { get; init; }

        public required FakeLogCollector Logs { get; init; }

        public required RecordingFilter Filter { get; init; }

        public required RecordingAuditRecorder Audit { get; init; }

        public required SpyEvaluator Evaluator { get; init; }

        public ICkMethodDispatcher Dispatcher => Provider.GetRequiredService<ICkMethodDispatcher>();

        /// <summary>What the bus consumer of this host answers for a request that arrived over the bus.</summary>
        public CkMethodHandlerInvoker BusInvoker => Provider.GetRequiredService<CkMethodHandlerInvoker>();

        public int BrokerCalls => Fake.GetCalls(EventHub).Count();

        public string AllLogText() => string.Join("\n", Logs.GetSnapshot().Select(r => r.Message + "\n" + r.Exception));

        public static async Task<SelfHost> StartAsync(ChangePasswordHandler handler,
            Action<ICkMethodHandlerRegistrationBuilder> configure, CkMethodDto? definition = null,
            CkMethodPrincipal? busPrincipal = null)
        {
            definition ??= TestData.Definition(true, "PASSWORD_TOO_SHORT");
            var logs = new FakeLogCollector();
            var eventHub = A.Fake<IDistributionEventHubService>();
            var filter = new RecordingFilter();
            var audit = new RecordingAuditRecorder();
            var evaluator = new SpyEvaluator();
            var resolver = A.Fake<ICkMethodDeclaringElementResolver>();
            A.CallTo(() => resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(eventHub);
            services.AddSingleton(A.Fake<IEventHubControl>());
            services.AddSingleton<ICkMethodDefinitionSource>(new FixedDefinitionSource(definition));
            services.AddSingleton<ICkMethodSensitivityResolver>(new DefinitionSensitivity(definition));
            services.AddSingleton<ICkMethodAuthorizationEvaluator>(evaluator);
            services.AddSingleton(resolver);
            // the bus path validates the forwarded token; the local path never does
            services.AddSingleton<ICkMethodCallerTokenValidator>(
                new FixedPrincipalValidator(busPrincipal ?? HostFixture.Administrator));
            services.AddSingleton<ICkMethodAuditRecorder>(audit);
            services.AddSingleton<ICkMethodInvocationFilter>(filter);
            services.AddScoped(_ => handler);

            services.AddCkMethodHandlers("SelfService", configure);
            services.AddCkMethodDispatcher();
            services.AddCkMethodAudit();

            var provider = services.BuildServiceProvider();
            // the startup validation (Order 15) fills the definitions of both registration sets
            await ActivatorUtilities.CreateInstance<CkMethodHandlerValidationService>(provider).InitializeAsync();
            await ActivatorUtilities.CreateInstance<CkInProcessMethodValidationService>(provider).InitializeAsync();

            return new SelfHost
            {
                Provider = provider, EventHub = eventHub, Logs = logs, Filter = filter, Audit = audit,
                Evaluator = evaluator
            };
        }
    }

    private static CkMethodRequest Request(string methodId = TestData.MethodId, string? parametersJson = null) =>
        TestData.Request(DateTime.UtcNow.AddSeconds(30), parametersJson: parametersJson) with { MethodId = methodId };

    private static string Json(CkMethodResponse response) => JsonSerializer.Serialize(response, Wire);

    private static async Task<CkMethodResponse> CallAsAsync(ICkMethodDispatcher dispatcher, CkMethodRequest request,
        CkMethodPrincipal? principal)
    {
        if (principal is null)
        {
            return await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);
        }

        using (CkMethodPrincipalScope.Begin(principal))
        {
            return await dispatcher.InvokeAsync(request, TestContext.Current.CancellationToken);
        }
    }

    // ---------------------------------------------------------------- bus-registered handler, called by its own host

    [Fact]
    public async Task BusHandler_CalledByItsHost_RunsInProcess_NothingGoesToTheBroker_TransportInProcessInMetricAndAudit()
    {
        // the instruments are process-wide: a method id of its own, so parallel tests cannot add measurements
        var methodId = $"Test.SelfCall{Guid.NewGuid():N}/User.ChangePassword-1";
        var handler = new ChangePasswordHandler();
        var host = await SelfHost.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(methodId));
        var calls = new List<(string? MethodId, string? Transport, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CkMethodTelemetry.MeterName && instrument.Name == CkMethodMetrics.CallsName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var t = tags.ToArray().ToDictionary(p => p.Key, p => p.Value?.ToString());
            lock (calls)
            {
                calls.Add((t.GetValueOrDefault(CkMethodMetrics.MethodIdTag), t.GetValueOrDefault(CkMethodMetrics.TransportTag),
                    t.GetValueOrDefault(CkMethodMetrics.OutcomeTag)));
            }
        });
        listener.Start();
        var request = Request(methodId);

        var response = await CallAsAsync(host.Dispatcher, request, HostFixture.Administrator);

        Assert.True(response.Success, Json(response));
        Assert.Equal("\"changed\"", response.ResultJson);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, host.BrokerCalls); // no send, no publish: the request never reaches the broker
        Assert.Equal([CkMethodTransportNames.InProcess], host.Filter.Contexts.Select(c => c.Transport));
        var record = Assert.Single(host.Audit.Records);
        Assert.Equal(CkMethodTransportNames.InProcess, record.Transport);
        Assert.Equal(request.InvocationId, record.InvocationId);
        lock (calls)
        {
            Assert.Equal([(methodId, CkMethodTransportNames.InProcess, "SUCCESS")], calls.Where(c => c.MethodId == methodId));
        }

        // the handler instance id is the announced one: the envelope is the one a bus caller gets
        Assert.Equal(host.Provider.GetRequiredService<CkMethodHandlerInstance>().InstanceId, response.HandlerInstanceId);
    }

    public static TheoryData<string> EnvelopeCases => ["success", "declared-error", "forbidden", "handler-failure"];

    [Theory]
    [MemberData(nameof(EnvelopeCases))]
    public async Task BusHandler_LocalCall_IsByteIdenticalToTheBusAnswerOfTheSameHost(string name)
    {
        var handler = new ChangePasswordHandler((context, _) => context.Parameters.Hint switch
        {
            "short" => Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(
                CkMethodOutcome<UserChangePasswordResult>.Fail("PASSWORD_TOO_SHORT", ("minLength", "8"))),
            "boom" => throw new InvalidOperationException("store failed"),
            _ => Task.FromResult<CkMethodOutcome<UserChangePasswordResult>>(new UserChangePasswordResult { Value = "changed" })
        });
        var definition = TestData.Definition(true, "PASSWORD_TOO_SHORT");
        definition.Authorization = new CkMethodAuthorizationDto { Roles = ["UserManagement"] };
        var caller = name == "forbidden"
            ? Stranger
            : HostFixture.Administrator with { Roles = ["UserManagement"] };
        var host = await SelfHost.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId), definition,
            busPrincipal: caller);
        var hint = name switch { "declared-error" => "short", "handler-failure" => "boom", _ => "ok" };
        var request = Request(parametersJson:
            $"{{\"currentPassword\":\"{TestData.OldSecret}\",\"newPassword\":\"{TestData.Secret}\",\"hint\":\"{hint}\"}}");

        var overBus = await host.BusInvoker.InvokeAsync(request, TestContext.Current.CancellationToken);
        var local = await CallAsAsync(host.Dispatcher, request, caller);

        Assert.Equal(Json(overBus), Json(local));
        Assert.Equal(0, host.BrokerCalls);
        switch (name)
        {
            case "success":
                Assert.True(local.Success);
                break;
            case "declared-error":
                Assert.Equal("PASSWORD_TOO_SHORT", local.Error!.Code);
                break;
            case "forbidden":
                Assert.Equal(CkMethodErrorCodes.MethodForbidden, local.Error!.Code);
                break;
            case "handler-failure":
                Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, local.Error!.Code);
                break;
        }
    }

    // ---------------------------------------------------------------- local only

    [Fact]
    public async Task LocalOnly_IsNotAnnounced_HasNoQueue_AndTheHostingProcessCallsIt()
    {
        var handler = new ChangePasswordHandler();
        var host = await SelfHost.StartAsync(handler, m => m
            .Handle<ChangePasswordHandler>(TestData.MethodId)
            .HandleLocalOnly<ChangePasswordHandler>(LocalOnlyMethodId));

        // the announcement and the queues (CkMethodHandlerStartupService connects one per instance method id) know only
        // the bus method
        var instance = host.Provider.GetRequiredService<CkMethodHandlerInstance>();
        Assert.Equal([TestData.MethodId], instance.MethodIds);
        Assert.Equal([TestData.MethodId], instance.CreateAnnouncement().MethodIds);
        Assert.DoesNotContain(LocalOnlyMethodId, host.Provider.GetRequiredService<CkMethodHandlerRegistrations>().Handlers.Keys);
        Assert.True(host.Provider.GetRequiredService<ICkInProcessMethodTransport>().CanHandle(LocalOnlyMethodId));

        var response = await CallAsAsync(host.Dispatcher, Request(LocalOnlyMethodId), HostFixture.Administrator);

        Assert.True(response.Success, Json(response));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, host.BrokerCalls);
        Assert.Equal(CkMethodTransportNames.InProcess, Assert.Single(host.Audit.Records).Transport);
    }

    [Fact]
    public async Task LocalOnly_CalledFromAnotherProcess_IsHandlerNotAvailable()
    {
        // another process: a dispatcher without the handler; the broker returns the unroutable message (no queue)
        var eventHub = A.Fake<IDistributionEventHubService>();
        A.CallTo(() => eventHub.GetCommandResponseAsync<CkMethodRequest, CkMethodResponse>(A<string>._,
                A<CkMethodRequest>._, A<CommandSendOptions>._, A<CancellationToken>._))
            .Throws(new CommandNotRoutableException("no queue bound"));
        var resolver = A.Fake<ICkMethodDeclaringElementResolver>();
        A.CallTo(() => resolver.Resolve(A<CkMethodRequest>._)).ReturnsLazily((CkMethodRequest r) => r.MethodId);
        var dispatcher = new CkMethodDispatcher(resolver,
            new CkMethodInvocationPipeline([], new CkMethodSensitivityResolver(null),
                NullLogger<CkMethodInvocationPipeline>.Instance),
            new CkMethodHandlerRegistry(TimeProvider.System), eventHub, new ServiceCollection().BuildServiceProvider(),
            new CkMethodSensitivityResolver(null), Options.Create(new CkMethodDispatcherOptions()), TimeProvider.System,
            NullLogger<CkMethodDispatcher>.Instance);

        var response = await CallAsAsync(dispatcher, Request(LocalOnlyMethodId), HostFixture.Administrator);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerNotAvailable, response.Error!.Code);
        Assert.Equal(LocalOnlyMethodId, response.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public async Task OnePlaceRule_BusAndLocalOnlyForTheSameMethod_StopsTheStart()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SelfHost.StartAsync(new ChangePasswordHandler(),
            m => m.Handle<ChangePasswordHandler>(TestData.MethodId).HandleLocalOnly<ChangePasswordHandler>(TestData.MethodId)));

        Assert.Contains(TestData.MethodId, error.Message);
        Assert.Contains("exactly one place", error.Message);
    }

    [Fact]
    public void OnePlaceRule_LocalOnlyAndInProcessForTheSameMethod_FailsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddCkMethodHandlers("SelfService", m => m.HandleLocalOnly<ChangePasswordHandler>(TestData.MethodId));

        Assert.Throws<InvalidOperationException>(() =>
            services.AddCkInProcessMethodHandlers(m => m.Handle<ChangePasswordHandler>(TestData.MethodId)));
    }

    [Fact]
    public async Task InProcessBuilder_HandleLocalOnly_IsAnInProcessRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<ICkMethodDefinitionSource>(new FixedDefinitionSource(TestData.Definition()));
        services.AddSingleton<ICkMethodSensitivityResolver>(new DefinitionSensitivity(TestData.Definition()));
        services.AddScoped(_ => new ChangePasswordHandler());
        services.AddCkInProcessMethodHandlers(m => m.HandleLocalOnly<ChangePasswordHandler>(TestData.MethodId));
        await using var provider = services.BuildServiceProvider();
        await ActivatorUtilities.CreateInstance<CkInProcessMethodValidationService>(provider).InitializeAsync();
        var transport = provider.GetRequiredService<ICkInProcessMethodTransport>();

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(HostFixture.Administrator))
        {
            response = await transport.InvokeAsync(Request(), TestContext.Current.CancellationToken);
        }

        Assert.True(response.Success, Json(response));
    }

    [Fact]
    public void BusHostWithoutLocalOnly_StillRegistersTheLocalTransport_ForItsOwnCalls()
    {
        var services = new ServiceCollection();
        services.AddCkMethodHandlers("SelfService", m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        Assert.Contains(services, d => d.ServiceType == typeof(ICkInProcessMethodTransport));
        var localOnly = (CkInProcessMethodRegistrations)services
            .Single(d => d.ServiceType == typeof(CkInProcessMethodRegistrations)).ImplementationInstance!;
        Assert.Empty(localOnly.Handlers);
    }

    // ---------------------------------------------------------------- authorization on the local path

    [Fact]
    public async Task LocalPath_WithoutPrincipal_IsForbidden_NeverSystem_AndTheHandlerDoesNotRun()
    {
        var handler = new ChangePasswordHandler();
        var host = await SelfHost.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));

        var response = await CallAsAsync(host.Dispatcher, Request(), principal: null);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, host.BrokerCalls);
        // the declared authorization saw no principal at all: no fallback identity was put in its place
        Assert.Equal([null], host.Evaluator.Principals);
    }

    [Fact]
    public async Task LocalPath_EnforcesTheDeclaredRoles_WithTheHostsEvaluator()
    {
        var handler = new ChangePasswordHandler();
        var definition = TestData.DefinitionWithRoles("UserManagement");
        var host = await SelfHost.StartAsync(handler, m => m.HandleLocalOnly<ChangePasswordHandler>(TestData.MethodId),
            definition);

        var refused = await CallAsAsync(host.Dispatcher, Request(), Stranger);
        var allowed = await CallAsAsync(host.Dispatcher, Request(),
            HostFixture.Administrator with { Roles = ["UserManagement"] });

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, refused.Error!.Code);
        Assert.True(allowed.Success, Json(allowed));
        Assert.Equal(1, handler.Calls);
        Assert.Equal([Stranger.SubjectId, HostFixture.Administrator.SubjectId],
            host.Evaluator.Principals.Select(p => p?.SubjectId));
    }

    // ---------------------------------------------------------------- redaction canary

    [Fact]
    public async Task LocalPath_SensitiveParameters_NeverReachLogsTracesOrFilters()
    {
        var handler = new ChangePasswordHandler((context, _) =>
            throw new InvalidOperationException($"store failed for {context.Parameters.NewPassword} / {context.Parameters.CurrentPassword}"));
        var host = await SelfHost.StartAsync(handler, m => m.Handle<ChangePasswordHandler>(TestData.MethodId));
        var traced = new List<string>();
        using var activities = new ActivityListener();
        activities.ShouldListenTo = _ => true;
        activities.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded;
        activities.ActivityStopped = a =>
        {
            lock (traced)
            {
                traced.Add(a.DisplayName + " " + string.Join(" ", a.TagObjects.Select(t => $"{t.Key}={t.Value}")) + " " +
                           string.Join(" ", a.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}"))));
            }
        };
        ActivitySource.AddActivityListener(activities);

        var response = await CallAsAsync(host.Dispatcher, Request(), HostFixture.Administrator);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        Assert.Contains("store failed", host.AllLogText()); // the failure is logged ...
        foreach (var secret in new[] { TestData.Secret, TestData.OldSecret, TestData.Token })
        {
            // ... with the canaries masked, and they are in no span, filter view, audit record or answer
            Assert.DoesNotContain(secret, host.AllLogText());
            lock (traced)
            {
                Assert.DoesNotContain(traced, t => t.Contains(secret, StringComparison.Ordinal));
            }

            Assert.DoesNotContain(host.Filter.Contexts, c => JsonSerializer.Serialize(c.Request, Wire).Contains(secret));
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(host.Audit.Records, Wire));
            Assert.DoesNotContain(secret, Json(response));
        }
    }
}
