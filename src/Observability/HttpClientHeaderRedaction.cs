using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Observability;

/// <summary>
/// Keeps HTTP header values and query strings out of the request and response logs that
/// <c>IHttpClientFactory</c> writes for every named and typed client.
/// </summary>
/// <remarks>
/// <para>
/// Invariant: for every client created through <c>IHttpClientFactory</c> in a host that calls
/// <c>AddObservability()</c>, no header value and no query string reaches a log at any level, neither in the
/// formatted message nor in the structured properties; header names stay.
/// </para>
/// <para>
/// Why the framework loggers are replaced: their header events
/// (<c>System.Net.Http.HttpClient.&lt;name&gt;.LogicalHandler</c> / <c>.ClientHandler</c>, Trace) redact only the
/// formatted message. The structured state of the same event carries every raw value, and a JSON layout that
/// writes event properties ships them, bearer tokens included. <c>ShouldRedactHeaderValue</c> does not change that.
/// </para>
/// <para>
/// How: an <see cref="IHttpMessageHandlerBuilderFilter"/> placed first in the service collection (so it wraps every
/// other filter, the framework's logging filter included) swaps each framework logging handler for a redacting one
/// with the same category, event ids, levels and messages. Clients configured with <c>RemoveAllLoggers()</c> have no
/// framework logging handler and stay silent; <c>AddDefaultLogger()</c> brings the framework handlers back, which are
/// swapped as well. A <c>PostConfigureAll</c> additionally forces <c>ShouldRedactHeaderValue</c> to redact every
/// header, so a host cannot narrow it.
/// </para>
/// <para>
/// Not covered: a client with its own <see cref="IHttpClientLogger"/> (<c>AddLogger</c>,
/// <c>AddExtendedHttpClientLogging</c>) logs what that logger decides and must redact on its own; a
/// <c>new HttpClient(...)</c> outside the factory has no logging handler at all.
/// </para>
/// </remarks>
public static class HttpClientHeaderRedaction
{
    /// <summary>
    /// The value written instead of every header value and every query string.
    /// </summary>
    public const string RedactedValue = "*";

    /// <summary>
    /// Registers the redaction for every <c>IHttpClientFactory</c> client of the service collection.
    /// Calling it more than once is harmless.
    /// </summary>
    public static IServiceCollection AddOctoHttpClientHeaderRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ImplementationType == typeof(RedactingHttpLoggingFilter)))
        {
            // First position: filters are applied outermost-first in registration order, and this one must see the
            // handlers the framework's logging filter adds, whenever the host registered that filter.
            services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, RedactingHttpLoggingFilter>());
            services.PostConfigureAll<HttpClientFactoryOptions>(options => options.ShouldRedactHeaderValue = _ => true);
        }

        return services;
    }

    /// <summary>
    /// Returns the URI as it may be logged: scheme, host, port and path; user info is dropped and a query string
    /// becomes <c>?*</c>.
    /// </summary>
    internal static string? RedactUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        if (uri.IsAbsoluteUri)
        {
            var withoutQuery = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            return string.IsNullOrEmpty(uri.Query) ? withoutQuery : withoutQuery + "?" + RedactedValue;
        }

        var text = uri.OriginalString;
        var cut = text.IndexOfAny(['?', '#']);
        if (cut < 0)
        {
            return text;
        }

        return text[cut] == '?' ? text[..cut] + "?" + RedactedValue : text[..cut];
    }
}

/// <summary>
/// Swaps the framework's HTTP client logging handlers for <see cref="RedactingHttpLoggingHandler"/>.
/// </summary>
internal sealed class RedactingHttpLoggingFilter(ILoggerFactory loggerFactory) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return builder =>
        {
            next(builder);

            var loggerName = string.IsNullOrEmpty(builder.Name) ? "Default" : builder.Name;
            var handlers = builder.AdditionalHandlers;
            for (var i = 0; i < handlers.Count; i++)
            {
                // Exact types: only the framework's own handlers are swapped, never a subclass or a custom logger.
                var type = handlers[i].GetType();
                if (type == typeof(LoggingScopeHttpMessageHandler))
                {
                    handlers[i] = new RedactingHttpLoggingHandler(
                        loggerFactory.CreateLogger($"System.Net.Http.HttpClient.{loggerName}.LogicalHandler"),
                        RedactingHttpLoggingHandler.Kind.Pipeline);
                }
                else if (type == typeof(LoggingHttpMessageHandler))
                {
                    handlers[i] = new RedactingHttpLoggingHandler(
                        loggerFactory.CreateLogger($"System.Net.Http.HttpClient.{loggerName}.ClientHandler"),
                        RedactingHttpLoggingHandler.Kind.Client);
                }
            }
        };
    }
}

/// <summary>
/// Writes the events of the framework's HTTP client logging handlers with every header value and query string
/// redacted, in the formatted message and in the structured properties.
/// </summary>
internal sealed class RedactingHttpLoggingHandler(ILogger logger, RedactingHttpLoggingHandler.Kind kind)
    : DelegatingHandler
{
    internal enum Kind
    {
        /// <summary>Outermost handler, category <c>.LogicalHandler</c>, opens a scope per request.</summary>
        Pipeline,

        /// <summary>Innermost handler, category <c>.ClientHandler</c>.</summary>
        Client
    }

    private static readonly Func<ILogger, HttpMethod, string?, IDisposable?> PipelineScope =
        LoggerMessage.DefineScope<HttpMethod, string?>("HTTP {HttpMethod} {Uri}");

    private static readonly Action<ILogger, HttpMethod, string?, Exception?> PipelineStart =
        LoggerMessage.Define<HttpMethod, string?>(LogLevel.Information, new EventId(100, "RequestPipelineStart"),
            "Start processing HTTP request {HttpMethod} {Uri}");

    private static readonly Action<ILogger, double, int, Exception?> PipelineEnd =
        LoggerMessage.Define<double, int>(LogLevel.Information, new EventId(101, "RequestPipelineEnd"),
            "End processing HTTP request after {ElapsedMilliseconds}ms - {StatusCode}");

    private static readonly Action<ILogger, double, Exception?> PipelineFailed =
        LoggerMessage.Define<double>(LogLevel.Information, new EventId(104, "RequestPipelineFailed"),
            "HTTP request failed after {ElapsedMilliseconds}ms");

    private static readonly Action<ILogger, HttpMethod, string?, Exception?> ClientStart =
        LoggerMessage.Define<HttpMethod, string?>(LogLevel.Information, new EventId(100, "RequestStart"),
            "Sending HTTP request {HttpMethod} {Uri}");

    private static readonly Action<ILogger, double, int, Exception?> ClientEnd =
        LoggerMessage.Define<double, int>(LogLevel.Information, new EventId(101, "RequestEnd"),
            "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}");

    private static readonly Action<ILogger, double, Exception?> ClientFailed =
        LoggerMessage.Define<double>(LogLevel.Information, new EventId(104, "RequestFailed"),
            "HTTP request failed after {ElapsedMilliseconds}ms");

    private static readonly EventId PipelineRequestHeader = new(102, "RequestPipelineRequestHeader");
    private static readonly EventId PipelineResponseHeader = new(103, "RequestPipelineResponseHeader");
    private static readonly EventId ClientRequestHeader = new(102, "RequestHeader");
    private static readonly EventId ClientResponseHeader = new(103, "ResponseHeader");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = HttpClientHeaderRedaction.RedactUri(request.RequestUri);
        using var scope = kind == Kind.Pipeline ? PipelineScope(logger, request.Method, uri) : null;

        (kind == Kind.Pipeline ? PipelineStart : ClientStart)(logger, request.Method, uri, null);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            var state = new RedactedHeadersLogValue("Request Headers:", request.Headers, request.Content?.Headers);
            logger.Log(LogLevel.Trace, kind == Kind.Pipeline ? PipelineRequestHeader : ClientRequestHeader, state,
                null, static (s, _) => s.ToString());
        }

        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (kind == Kind.Pipeline ? PipelineFailed : ClientFailed)(logger,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception);
            throw;
        }

        (kind == Kind.Pipeline ? PipelineEnd : ClientEnd)(logger,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, (int)response.StatusCode, null);
        if (logger.IsEnabled(LogLevel.Trace))
        {
            var state = new RedactedHeadersLogValue("Response Headers:", response.Headers, response.Content?.Headers);
            logger.Log(LogLevel.Trace, kind == Kind.Pipeline ? PipelineResponseHeader : ClientResponseHeader, state,
                null, static (s, _) => s.ToString());
        }

        return response;
    }

    /// <summary>
    /// Header names with <see cref="HttpClientHeaderRedaction.RedactedValue"/> as value, both in
    /// <see cref="ToString"/> and in the structured properties.
    /// </summary>
    private sealed class RedactedHeadersLogValue : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly string _title;
        private readonly List<KeyValuePair<string, object?>> _values;

        public RedactedHeadersLogValue(string title, HttpHeaders headers, HttpHeaders? contentHeaders)
        {
            _title = title;
            _values = headers
                .Concat(contentHeaders ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .Select(header => new KeyValuePair<string, object?>(header.Key, HttpClientHeaderRedaction.RedactedValue))
                .ToList();
        }

        public int Count => _values.Count;

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var text = new StringBuilder(_title).AppendLine();
            foreach (var (name, value) in _values)
            {
                text.Append(name).Append(": ").Append(value).AppendLine();
            }

            return text.ToString();
        }
    }
}
