using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests;

/// <summary>
///     AB#6914 canary scan through the real pipeline against a private RabbitMQ: a sensitive parameter and the caller
///     token carry <c>CANARY-SECRET-123</c> through the bus and the in-process transport, on the success path, a failing
///     handler, a timeout and a validation error. Everything an operator could ship elsewhere is captured: all log
///     output (messages, exceptions, scopes), the audit view of the outermost filter, activity tags and events, and the
///     responses. The canary must appear nowhere except in the one place that needs it, the handler.
/// </summary>
[Collection(RabbitMqCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CkMethodRedactionBusTests(RabbitMqFixture rabbitMq) : IAsyncLifetime
{
    private const string Canary = "CANARY-SECRET-123";

    /// <summary>
    ///     A canary that JSON escapes (AB#7015): quote, backslash, plus, ampersand, angle brackets, apostrophe, slash, a
    ///     non-ASCII letter, a surrogate pair and control characters. The first canary has none of these and could not catch
    ///     an escaped copy of the value in an exception message.
    /// </summary>
    private const string EscapingCanary = "CANARY-\"q\\b+&<>'/-\u00c4\u00f6-\U0001F600-\t\u0007-END";

    private const string TokenCanary = "CANARY-TOKEN-456";
    private const string MethodId = "Sample.Accounts/Account.ChangePassword-1";
    private const string SecretMethodId = "Sample.Accounts/Account.IssueApiKey-1";

    /// <summary>The value the sensitive result of <see cref="SecretMethodId" /> carries (AB#7007).</summary>
    private const string ResultCanary = "CANARY-RESULT-789-\"q\\b+&<>/-\u00c4";
    private const string Tenant = "meshtest";
    private const string Target = "65f0c0ffee65f0c0ffee65f0";

    private readonly string _instancePrefix = $"redact-{Guid.NewGuid():N}";
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<IEventHubControl> _hubs = [];
    private readonly CaptureSink _sink = new();
    private ActivityListener? _listener;
    private System.Diagnostics.Metrics.MeterListener? _meters;

    public ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _sink.Add("activity", DescribeActivity(activity))
        };
        ActivitySource.AddActivityListener(_listener);
        // AB#6920: every metric measurement of the method meter (instrument name + all tags) is scanned like a log line
        _meters = new System.Diagnostics.Metrics.MeterListener();
        _meters.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CkMethodTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => AddMetric(i, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => AddMetric(i, tags));
        _meters.Start();
        return ValueTask.CompletedTask;
    }

    private void AddMetric(System.Diagnostics.Metrics.Instrument instrument,
        ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _sink.Add("metric", instrument.Name + " " + string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")));

    public async ValueTask DisposeAsync()
    {
        _meters?.Dispose();
        _listener?.Dispose();
        foreach (var hub in _hubs)
        {
            try
            {
                await hub.StopAsync();
            }
            catch
            {
                // best effort
            }
        }

        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }
    }

    public sealed record AccountChangePasswordParameters
    {
        public required string NewPassword { get; init; }

        public string Mode { get; init; } = "ok";
    }

    public sealed class ChangePasswordHandler : ICkMethodHandler<AccountChangePasswordParameters>
    {
        public static int Calls;

        public static string? SeenPassword;

        public static string? SeenToken;

        public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<AccountChangePasswordParameters> context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            SeenPassword = context.Parameters.NewPassword;
            SeenToken = context.CallerAccessToken;
            switch (context.Parameters.Mode)
            {
                case "fail":
                    throw new InvalidOperationException(
                        $"password store rejected {context.Parameters.NewPassword} for token {context.CallerAccessToken}");
                case "failjson":
                    // a serializer exception that quotes the payload in its JSON-escaped form
                    throw new InvalidOperationException(
                        "payload rejected " + JsonSerializer.Serialize(new { newPassword = context.Parameters.NewPassword }) +
                        " relaxed " + JsonSerializer.Serialize(new { newPassword = context.Parameters.NewPassword },
                            RelaxedEscaping));
                case "hang":
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    break;
            }

            return CkMethodOutcome.Ok();
        }
    }

    public sealed record IssueApiKeyParameters;

    public sealed record IssueApiKeyResult
    {
        public required string ApiKey { get; init; }
    }

    public sealed class IssueApiKeyHandler : ICkMethodHandler<IssueApiKeyParameters, IssueApiKeyResult>
    {
        public Task<CkMethodOutcome<IssueApiKeyResult>> HandleAsync(CkMethodContext<IssueApiKeyParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<IssueApiKeyResult>>(new IssueApiKeyResult { ApiKey = ResultCanary });
    }

    private static readonly CkMethodDto SecretDefinition = new()
    {
        MethodId = "IssueApiKey-1",
        Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String, Sensitive = true },
        Authorization = new CkMethodAuthorizationDto { Roles = ["R"] }
    };

    private static readonly JsonSerializerOptions RelaxedEscaping = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly CkMethodDto Definition = new()
    {
        MethodId = "ChangePassword-1",
        Parameters =
        [
            new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "mode", ValueType = AttributeValueTypesDto.String, IsOptional = true }
        ],
        Authorization = new CkMethodAuthorizationDto { Roles = ["R"] }
    };

    private sealed class DefinitionSource : ICkMethodDefinitionSource, ICkMethodAuthorizationDefinitionSource
    {
        public IReadOnlyList<string> TenantIds { get; } = ["octosystem"];

        public CkMethodDto? Find(string qualifiedMethodId) =>
            qualifiedMethodId == MethodId ? Definition : qualifiedMethodId == SecretMethodId ? SecretDefinition : null;

        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => Find(qualifiedMethodId);
    }

    /// <summary>The audit filter of F3.5 stand-in: outermost, writes down everything it is allowed to see.</summary>
    private sealed class AuditProbeFilter(CaptureSink sink) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Audit;

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            sink.Add("audit-request", JsonSerializer.Serialize(context.Request));
            sink.Add("audit-context", context.ToString());
            var response = await next(context.Request);
            sink.Add("audit-response", JsonSerializer.Serialize(response));
            return response;
        }
    }

    /// <summary>The sink behind the real audit recorder (AB#6919): every event that the audit filter writes is scanned.</summary>
    private sealed class EventScanSink(CaptureSink sink) : Meshmakers.Octo.Runtime.Contracts.AuditTrails.IAuditEventSink
    {
        public Task PublishAsync(Meshmakers.Octo.Runtime.Contracts.AuditTrails.AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            sink.Add("audit-event", auditEvent.Category + "|" + auditEvent.Message + "|" +
                                    JsonSerializer.Serialize(auditEvent.Metadata));
            return Task.CompletedTask;
        }
    }

    private async Task<ICkMethodDispatcher> StartAsync(bool inProcess)
    {
        var definition = new DefinitionSource();
        if (!inProcess)
        {
            await StartHandlerHostAsync(definition);
        }

        var services = BaseServices();
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddSingleton<ICkMethodAuthorizationDefinitionSource>(definition);
        services.AddSingleton<ICkMethodInvocationFilter>(new AuditProbeFilter(_sink));
        services.AddSingleton<Meshmakers.Octo.Runtime.Contracts.AuditTrails.IAuditEventSink>(new EventScanSink(_sink));
        services.AddCkMethodAudit();
        services.AddCkMethodAuthorization();
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "Gateway" + (inProcess ? "InProcess" : "Bus");
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerRegistryFeed();
        });
        services.AddCkMethodDispatcher();
        if (inProcess)
        {
            services.AddSingleton<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
            services.AddCkInProcessMethodHandlers(m =>
            {
                m.Handle<ChangePasswordHandler>(MethodId);
                m.Handle<IssueApiKeyHandler>(SecretMethodId);
            });
        }

        var provider = await StartHubAsync(services);
        return provider.GetRequiredService<ICkMethodDispatcher>();
    }

    private async Task StartHandlerHostAsync(DefinitionSource definition)
    {
        var services = BaseServices();
        services.AddSingleton<ICkMethodDefinitionSource>(definition);
        services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = "HandlerA";
            c.AutomaticallyStartBusDuringStartup = false;
            c.AddCkMethodHandlerDiscovery();
        });
        services.AddSingleton<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator, AllowAnyCallerEvaluator>();
        services.AddCkMethodHandlers("HandlerA", m =>
        {
            m.Handle<ChangePasswordHandler>(MethodId);
            m.Handle<IssueApiKeyHandler>(SecretMethodId);
        });
        var provider = await StartHubAsync(services);
        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is CkMethodHandlerHeartbeatService))
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private async Task<ServiceProvider> StartHubAsync(ServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var hub = provider.GetRequiredService<IEventHubControl>();
        _hubs.Add(hub);
        await hub.StartAsync();
        using var scope = provider.CreateScope();
        foreach (var service in scope.ServiceProvider.GetServices<IAsyncInitializationService>().OrderBy(s => s.Order))
        {
            await service.InitializeAsync();
        }

        return provider;
    }

    private ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new CaptureLoggerProvider(_sink)));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = _instancePrefix;
            o.BrokerHost = rabbitMq.Host;
            o.BrokerPort = (ushort)rabbitMq.Port;
            o.BrokerUser = "guest";
            o.BrokerPassword = "guest";
        });
        return services;
    }

    private static CkMethodRequest NewRequest(string parametersJson, TimeSpan timeout, string methodId = MethodId) => new(Tenant)
    {
        InvocationId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        MethodId = methodId,
        CkTypeId = "Sample.Accounts/Account",
        TargetRtId = Target,
        ParametersJson = parametersJson,
        CallerAccessToken = TokenCanary,
        DeadlineUtc = DateTime.UtcNow + timeout
    };

    private static Task<CkMethodResponse> CallAsync(ICkMethodDispatcher dispatcher, string parametersJson,
        TimeSpan? timeout = null, string methodId = MethodId)
    {
        using (CkMethodPrincipalScope.Begin(new CkMethodPrincipal("65f0c0ffee65f0c0ffee65f1", ["R"],
                   [CkMethodAuthorizationOptions.ApiScope])))
        {
            return dispatcher.InvokeAsync(NewRequest(parametersJson, timeout ?? TimeSpan.FromSeconds(10), methodId),
                TestContext.Current.CancellationToken);
        }
    }

    public static TheoryData<bool, string> Cases => new()
    {
        { false, Canary },
        { true, Canary },
        { false, EscapingCanary },
        { true, EscapingCanary }
    };

    private static string Params(string secret, string? mode = null) =>
        JsonSerializer.Serialize(mode == null
            ? new Dictionary<string, string> { ["newPassword"] = secret }
            : new Dictionary<string, string> { ["newPassword"] = secret, ["mode"] = mode });

    /// <summary>The plain value and the escaped forms of System.Text.Json (default and relaxed encoder).</summary>
    private static string[] Forms(string secret) =>
    [
        secret,
        JsonSerializer.Serialize(secret)[1..^1],
        JsonSerializer.Serialize(secret, RelaxedEscaping)[1..^1]
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Canary_NeverAppearsInLogsAuditActivitiesOrResponses_OnAnyPath(bool inProcess, string canary)
    {
        var dispatcher = await StartAsync(inProcess);
        ChangePasswordHandler.SeenPassword = null;
        ChangePasswordHandler.SeenToken = null;
        var responses = new List<CkMethodResponse>();

        // (a) success
        var ok = await CallAsync(dispatcher, Params(canary));
        Assert.True(ok.Success, ok.Error?.Code);
        // the one place that must get the raw values
        Assert.Equal(canary, ChangePasswordHandler.SeenPassword);
        Assert.Equal(TokenCanary, ChangePasswordHandler.SeenToken);
        responses.Add(ok);

        // (c) failing handler quoting both values in its exception message
        var failed = await CallAsync(dispatcher, Params(canary, "fail"));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, failed.Error!.Code);
        responses.Add(failed);

        // (c2) failing handler quoting the value in its JSON-escaped form (AB#7015)
        var failedJson = await CallAsync(dispatcher, Params(canary, "failjson"));
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, failedJson.Error!.Code);
        responses.Add(failedJson);

        // (d) timeout
        var timedOut = await CallAsync(dispatcher, Params(canary, "hang"),
            TimeSpan.FromSeconds(1));
        Assert.Equal(CkMethodErrorCodes.MethodTimeout, timedOut.Error!.Code);
        responses.Add(timedOut);

        // (e) validation errors: wrong shape and truncated JSON, both holding the canary
        var wrongShape = await CallAsync(dispatcher, JsonSerializer.Serialize(new { newPassword = new { inner = canary } }));
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, wrongShape.Error!.Code);
        responses.Add(wrongShape);
        var truncated = await CallAsync(dispatcher, "{\"newPassword\":\"" + JsonSerializer.Serialize(canary)[1..^1]);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, truncated.Error!.Code);
        responses.Add(truncated);

        // let late log lines (handler side, bus consumer) arrive
        await Task.Delay(500, TestContext.Current.CancellationToken);

        foreach (var response in responses)
        {
            _sink.Add("response", JsonSerializer.Serialize(response));
        }

        var captured = _sink.Snapshot();
        Assert.Contains(captured, c => c.Kind == "audit-request"); // the scan really looked at the audit view
        Assert.Contains(captured, c => c.Kind == "log");
        // AB#6920: the scan covers the method meter: the calls were counted, and no label carries the canary
        Assert.Contains(captured, c => c.Kind == "metric" && c.Text.StartsWith("ck.method.calls ", StringComparison.Ordinal));
        Assert.Contains(captured, c => c.Kind == "metric" && c.Text.StartsWith("ck.method.duration ", StringComparison.Ordinal));
        // the real audit filter wrote one event per call (6 calls above), and the scan below covers them
        Assert.Equal(6, captured.Count(c => c.Kind == "audit-event"));
        var forms = Forms(canary);
        var leaks = captured.Where(c => forms.Any(f => c.Text.Contains(f, StringComparison.Ordinal)) ||
                                        c.Text.Contains(Canary, StringComparison.Ordinal) ||
                                        c.Text.Contains(TokenCanary, StringComparison.Ordinal)).ToList();
        Assert.True(leaks.Count == 0,
            "canary found in: " + string.Join(" | ", leaks.Select(l => $"{l.Kind}: {Truncate(l.Text)}")));
    }

    /// <summary>
    ///     AB#7007: the result of a method marked <c>sensitive</c> reaches the caller, byte for byte, and appears in no log
    ///     line, no audit record of the outermost filter and no activity, over the real bus and in-process.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensitiveResult_ReachesTheCaller_ButNeverLogsAuditOrActivities(bool inProcess)
    {
        var dispatcher = await StartAsync(inProcess);

        var response = await CallAsync(dispatcher, "{}", methodId: SecretMethodId);

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(ResultCanary, JsonDocument.Parse(response.ResultJson!).RootElement.GetProperty("apiKey").GetString());
        await Task.Delay(500, TestContext.Current.CancellationToken);
        _sink.Add("response-text", response.ToString());

        var captured = _sink.Snapshot();
        Assert.Contains(captured, c => c.Kind == "audit-response"); // the scan looked at the audit view
        Assert.Contains(captured, c => c.Kind == "log");
        var forms = Forms(ResultCanary);
        var leaks = captured.Where(c => forms.Any(f => c.Text.Contains(f, StringComparison.Ordinal))).ToList();
        Assert.True(leaks.Count == 0,
            "result found in: " + string.Join(" | ", leaks.Select(l => $"{l.Kind}: {Truncate(l.Text)}")));
        Assert.Contains(captured, c => c.Kind == "audit-response" && c.Text.Contains("***", StringComparison.Ordinal));
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] : text;

    private static string DescribeActivity(Activity activity) =>
        string.Join(" ", new[] { activity.DisplayName, activity.OperationName, activity.StatusDescription }
            .Concat(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"))
            .Concat(activity.Baggage.Select(b => $"{b.Key}={b.Value}"))
            .Concat(activity.Events.Select(e => e.Name + string.Join(",", e.Tags.Select(t => $"{t.Key}={t.Value}")))));

    private sealed record Captured(string Kind, string Text);

    private sealed class CaptureSink
    {
        private readonly ConcurrentQueue<Captured> _items = new();

        public void Add(string kind, string text) => _items.Enqueue(new Captured(kind, text));

        public List<Captured> Snapshot() => _items.ToList();
    }

    private sealed class CaptureLoggerProvider(CaptureSink sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, sink);

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogger(string category, CaptureSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            sink.Add("log", $"scope[{category}] {Describe(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add("log", $"{category} {logLevel} {formatter(state, exception)} {Describe(state)} {exception}");

        private static string Describe<TState>(TState state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(",", pairs.Select(p => $"{p.Key}={p.Value}"))
                : state?.ToString() ?? string.Empty;
    }
}
