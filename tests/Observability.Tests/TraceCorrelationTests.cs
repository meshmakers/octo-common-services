using System.Diagnostics;

using Meshmakers.Octo.Services.Observability;

using NLog;
using NLog.Layouts;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     Pins that the NLog layout renderers added for AB#5478 §2.3 actually produce trace context.
/// </summary>
/// <remarks>
///     <para>
///         Trace correlation for logs has two halves and both fail silently. In the process, a
///         layout renderer that resolves to nothing still produces a perfectly valid log line — just
///         one that can never be joined to a trace. In the collector, a <c>trace_id</c> that sits in
///         the log body as an ordinary field is <b>not</b> trace context: OTLP keeps trace id and
///         span id as dedicated top-level <c>LogRecord</c> fields, and a backend will not correlate
///         on an attribute that merely has the same name. The collector half lives in the
///         <c>Dash0Monitoring</c> transform in <c>meshmakers-infrastructure</c>; this file covers the
///         process half.
///     </para>
///     <para>
///         The registration path under test is the one production uses: <c>RegisterAssembly</c>, the
///         programmatic equivalent of <c>&lt;extensions&gt;&lt;add assembly="…" /&gt;</c> in
///         <c>nlog.config</c>. Registering the two renderers by type instead would pass while the
///         real configuration silently rendered <c>${otel-trace-id}</c> as the literal text.
///     </para>
/// </remarks>
public class TraceCorrelationTests
{
    public TraceCorrelationTests()
    {
        LogManager.Setup().SetupExtensions(extensions =>
            extensions.RegisterAssembly(typeof(OtelTraceIdLayoutRenderer).Assembly));
    }

    [Fact]
    public void TraceIdRenderer_InsideAnActivity_RendersThe32CharacterHexId()
    {
        using var activity = new Activity("octo.test").Start();

        var rendered = Render("${otel-trace-id}");

        Assert.Equal(activity.TraceId.ToHexString(), rendered);
        Assert.Equal(32, rendered.Length);
        Assert.Matches("^[0-9a-f]{32}$", rendered);
    }

    [Fact]
    public void SpanIdRenderer_InsideAnActivity_RendersThe16CharacterHexId()
    {
        using var activity = new Activity("octo.test").Start();

        var rendered = Render("${otel-span-id}");

        Assert.Equal(activity.SpanId.ToHexString(), rendered);
        Assert.Equal(16, rendered.Length);
        Assert.Matches("^[0-9a-f]{16}$", rendered);
    }

    /// <summary>
    ///     Outside an activity both renderers must yield the empty string, never a placeholder.
    /// </summary>
    /// <remarks>
    ///     This is what keeps a background-service log line out of trouble. The collector-side
    ///     guard only promotes an id that matches the full hex length, so an empty field is skipped
    ///     and the record keeps no trace context — whereas a literal such as <c>(null)</c> or
    ///     <c>00000000000000000000000000000000</c> would be written into the record and would
    ///     correlate every untraced log line of the fleet to the same non-existent trace.
    /// </remarks>
    [Fact]
    public void BothRenderers_WithNoCurrentActivity_RenderNothing()
    {
        Assert.Null(Activity.Current);

        Assert.Equal(string.Empty, Render("${otel-trace-id}"));
        Assert.Equal(string.Empty, Render("${otel-span-id}"));
    }

    /// <summary>
    ///     The renderers have to work inside a <see cref="JsonLayout" />, because that is the only
    ///     place they are ever used, and an unresolved renderer behaves differently there: the field
    ///     is omitted rather than rendered empty.
    /// </summary>
    [Fact]
    public void JsonLayout_CarriesTheTraceFields_OnlyWhileAnActivityIsCurrent()
    {
        var layout = new JsonLayout
        {
            Attributes =
            {
                new JsonAttribute("message", "${message}"),
                new JsonAttribute("trace_id", "${otel-trace-id}"),
                new JsonAttribute("span_id", "${otel-span-id}")
            }
        };

        string withActivity;
        using (var activity = new Activity("octo.test").Start())
        {
            withActivity = layout.Render(LogEventInfo.Create(LogLevel.Info, "octo", "hello"));
            Assert.Contains($"\"trace_id\":\"{activity.TraceId.ToHexString()}\"", withActivity);
            Assert.Contains($"\"span_id\":\"{activity.SpanId.ToHexString()}\"", withActivity);
        }

        var withoutActivity = layout.Render(LogEventInfo.Create(LogLevel.Info, "octo", "hello"));

        Assert.DoesNotContain("trace_id", withoutActivity);
        Assert.DoesNotContain("span_id", withoutActivity);
        Assert.Contains("\"message\":\"hello\"", withoutActivity);
    }

    private static string Render(string layout)
    {
        return Layout.FromString(layout).Render(LogEventInfo.CreateNullEvent());
    }
}
