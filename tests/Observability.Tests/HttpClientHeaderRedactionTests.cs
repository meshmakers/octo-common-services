using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Meshmakers.Octo.Services.Observability;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     Pins that <c>AddObservability()</c> keeps HTTP header values and query strings out of the
///     <c>IHttpClientFactory</c> request and response logs at every level.
/// </summary>
/// <remarks>
///     The framework's logging handlers redact header values only in the formatted message; the
///     structured state of the header events carries the raw values, and a JSON layout that writes
///     event properties ships them. Each test sends credentials through a stub primary handler at
///     Trace and inspects what a capturing provider received: every formatted message, every scope and
///     every structured property, collections flattened. Header names must appear, so a test cannot
///     pass because nothing was logged.
/// </remarks>
public class HttpClientHeaderRedactionTests
{
    private const string ClientName = "redaction-probe";

    private static readonly Dictionary<string, string> RequestHeaders = new()
    {
        ["Authorization"] = "Bearer secret-bearer-token-1c9e",
        ["Proxy-Authorization"] = "Basic secret-proxy-credential-5d2b",
        ["Cookie"] = "session=secret-cookie-value-8a41",
        ["X-Transfer-Key"] = "secret-transfer-key-3e77",
        ["X-Api-Key"] = "secret-api-key-44c1"
    };

    private const string SetCookieValue = "session=secret-set-cookie-value-9b06; Path=/; HttpOnly";
    private const string ResponseCustomValue = "secret-response-custom-2f80";
    private const string QueryToken = "secret-query-token-6d15";
    private const string ContentHeaderValue = "secret-content-token-a3d9";

    private static IEnumerable<string> AllSecrets() =>
        RequestHeaders.Values.Append(SetCookieValue).Append(ResponseCustomValue).Append(QueryToken).Append(ContentHeaderValue).Select(SecretPart);

    [Fact]
    public async Task WithoutAddObservability_TheFrameworkLogsRawValuesInStructuredState()
    {
        // Control: the leak exists and the capture sees it, so the other tests cannot pass vacuously.
        var logs = new CapturingLoggerProvider();
        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, logs);
        builder.Services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
        using var host = builder.Build();

        await SendProbeAsync(host);

        Assert.Contains(SecretPart(RequestHeaders["Authorization"]), logs.AllText());
    }

    [Fact]
    public async Task AddObservability_AtTrace_RedactsEveryHeaderValueAndTheQuery_InMessageAndStructuredState()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));

        await SendProbeAsync(host);

        var text = logs.AllText();
        AssertNoSecret(text);
        AssertHeaderNamesLogged(text);
        Assert.Contains("http://probe.invalid/resource?*", text);
    }

    [Fact]
    public async Task AddObservability_KeepsTheFrameworkCategoriesAndEventIds()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler()));

        await SendProbeAsync(host);

        var logical = $"System.Net.Http.HttpClient.{ClientName}.LogicalHandler";
        var client = $"System.Net.Http.HttpClient.{ClientName}.ClientHandler";
        Assert.Equal(
            [
                (logical, 100, LogLevel.Information), (logical, 102, LogLevel.Trace),
                (client, 100, LogLevel.Information), (client, 102, LogLevel.Trace),
                (client, 101, LogLevel.Information), (client, 103, LogLevel.Trace),
                (logical, 101, LogLevel.Information), (logical, 103, LogLevel.Trace)
            ],
            logs.Events.Where(e => e.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal))
                .Select(e => (e.Category, e.EventId, e.Level)));
    }

    [Fact]
    public async Task HostThatNarrowsThePredicate_StillGetsEveryValueRedacted()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler())
            .RedactLoggedHeaders(_ => false));

        await SendProbeAsync(host);

        var text = logs.AllText();
        AssertNoSecret(text);
        AssertHeaderNamesLogged(text);
        Assert.True(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(ClientName).ShouldRedactHeaderValue("X-Test"));
    }

    [Fact]
    public async Task ClientRegisteredBeforeAddObservability_IsCovered()
    {
        var logs = new CapturingLoggerProvider();
        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, logs);
        builder.Services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler())
            .RedactLoggedHeaders(_ => false);
        builder.AddObservability();
        using var host = builder.Build();

        await SendProbeAsync(host);

        var text = logs.AllText();
        AssertNoSecret(text);
        AssertHeaderNamesLogged(text);
    }

    [Fact]
    public async Task AddDefaultLogger_AfterRemoveAllLoggers_GetsTheRedactingLoggerAgain()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler())
            .RemoveAllLoggers()
            .AddDefaultLogger());

        await SendProbeAsync(host);

        var text = logs.AllText();
        AssertNoSecret(text);
        AssertHeaderNamesLogged(text);
    }

    [Fact]
    public async Task RemoveAllLoggers_StaysAllowed_AndLogsNothing()
    {
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler())
            .RemoveAllLoggers());

        await SendProbeAsync(host);

        Assert.DoesNotContain(logs.Events, e => e.Category.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal));
        AssertNoSecret(logs.AllText());
    }

    [Fact]
    public async Task CustomHttpClientLogger_IsNotReplaced_AndSeesTheRawRequest()
    {
        // Documented gap: a client's own IHttpClientLogger decides what it logs and must redact itself.
        var custom = new RecordingHttpClientLogger();
        var logs = new CapturingLoggerProvider();
        using var host = BuildHost(logs, services => services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler())
            .AddLogger(_ => custom));

        await SendProbeAsync(host);

        Assert.Contains(SecretPart(RequestHeaders["Authorization"]), custom.SeenAuthorization);
        // The framework loggers of the same client are still swapped.
        AssertNoSecret(logs.AllText());
        AssertHeaderNamesLogged(logs.AllText());
    }

    [Fact]
    public void AddOctoHttpClientHeaderRedaction_Twice_RegistersTheFilterOnceAndFirst()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddOctoHttpClientHeaderRedaction();
        services.AddOctoHttpClientHeaderRedaction();

        var filters = services.Where(d => d.ServiceType == typeof(IHttpMessageHandlerBuilderFilter)).ToList();
        Assert.Equal(1, filters.Count(d => d.ImplementationType?.Name == "RedactingHttpLoggingFilter"));
        Assert.Equal("RedactingHttpLoggingFilter", filters[0].ImplementationType?.Name);
    }

    [Theory]
    [InlineData("https://host.invalid/a/b?token=x", "https://host.invalid/a/b?*")]
    [InlineData("https://user:pass@host.invalid:8443/a", "https://host.invalid:8443/a")]
    [InlineData("https://host.invalid/a#frag", "https://host.invalid/a")]
    [InlineData("https://host.invalid/a", "https://host.invalid/a")]
    public void RedactUri_DropsUserInfoAndQuery_Absolute(string input, string expected)
    {
        Assert.Equal(expected, HttpClientHeaderRedaction.RedactUri(new Uri(input)));
    }

    [Theory]
    [InlineData("a/b?token=x", "a/b?*")]
    [InlineData("a/b#frag?x", "a/b")]
    [InlineData("a/b", "a/b")]
    public void RedactUri_DropsQuery_Relative(string input, string expected)
    {
        Assert.Equal(expected, HttpClientHeaderRedaction.RedactUri(new Uri(input, UriKind.Relative)));
    }

    private static IHost BuildHost(CapturingLoggerProvider logs, Action<IServiceCollection> registerClient)
    {
        var builder = Host.CreateApplicationBuilder();
        ConfigureLogging(builder, logs);
        builder.AddObservability();
        registerClient(builder.Services);
        return builder.Build();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, CapturingLoggerProvider logs)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Trace);
    }

    private static async Task SendProbeAsync(IHost host)
    {
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://probe.invalid/resource?access_token={QueryToken}");
        foreach (var (name, value) in RequestHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Content = new StringContent("probe");
        request.Content.Headers.TryAddWithoutValidation("X-Content-Token", ContentHeaderValue);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static void AssertNoSecret(string text)
    {
        foreach (var secret in AllSecrets())
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    private static void AssertHeaderNamesLogged(string text)
    {
        foreach (var name in RequestHeaders.Keys.Append("X-Content-Token").Append("Set-Cookie").Append("X-Response-Custom"))
        {
            Assert.Contains($"{name}: {HttpClientHeaderRedaction.RedactedValue}", text);
            Assert.Contains($"{name}={HttpClientHeaderRedaction.RedactedValue}", text);
        }
    }

    // The distinctive part of a value, so a match does not depend on how a logger joins or quotes it.
    private static string SecretPart(string value) =>
        value.Split(' ', '=', ';').First(part => part.StartsWith("secret-", StringComparison.Ordinal));

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            response.Headers.TryAddWithoutValidation("Set-Cookie", SetCookieValue);
            response.Headers.TryAddWithoutValidation("X-Response-Custom", ResponseCustomValue);
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHttpClientLogger : IHttpClientLogger
    {
        public string SeenAuthorization { get; private set; } = string.Empty;

        public object? LogRequestStart(HttpRequestMessage request)
        {
            SeenAuthorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return null;
        }

        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response,
            TimeSpan elapsed)
        {
        }

        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response,
            Exception exception, TimeSpan elapsed)
        {
        }
    }

    private sealed record CapturedEvent(string Category, int EventId, LogLevel Level);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();
        private readonly ConcurrentQueue<CapturedEvent> _events = new();

        public IReadOnlyCollection<CapturedEvent> Events => _events.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, _events);

        public string AllText() => string.Join("\n", _entries);

        public void Dispose()
        {
        }

        private static string Flatten(object? value) => value switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable items => string.Join(",", items.Cast<object?>().Select(Flatten)),
            _ => value.ToString() ?? string.Empty
        };

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<string> entries,
            ConcurrentQueue<CapturedEvent> events) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                // Scopes carry structured state too (the logical handler opens one per request).
                Capture(state, null);
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                events.Enqueue(new CapturedEvent(category, eventId.Id, logLevel));
                Capture(state, formatter(state, exception));
            }

            private void Capture<TState>(TState state, string? formatted)
            {
                if (formatted is not null)
                {
                    entries.Enqueue(formatted);
                }

                entries.Enqueue(state?.ToString() ?? string.Empty);
                if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                {
                    foreach (var property in properties)
                    {
                        // Structured sinks (JSON layouts, OTLP log export) serialize these values, not the
                        // formatted message, so a collection is flattened to its items.
                        entries.Enqueue($"{property.Key}={Flatten(property.Value)}");
                    }
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
