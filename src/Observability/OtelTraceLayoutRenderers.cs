using System.Diagnostics;
using System.Text;
using NLog;
using NLog.LayoutRenderers;

namespace Meshmakers.Octo.Services.Observability;

/// <summary>
/// Renders the trace id of the ambient <see cref="Activity" /> as 32 lowercase hex characters,
/// or nothing when no activity is current.
/// </summary>
/// <remarks>
/// <para>
/// AB#5478 section 2.3. NLog 6.2 core ships no activity layout renderer — verified by
/// inspecting <c>NLog.dll</c>, which contains no reference to <c>Activity</c> at all — and the
/// maintained <c>NLog.DiagnosticSource</c> package would add a dependency plus an assembly
/// auto-load question for a renderer this small. So it lives here, next to the rest of the
/// telemetry wiring, and <c>TraceCorrelationTests</c> pins that it actually renders: a layout
/// renderer that silently yields an empty string is the exact failure mode this epic exists
/// to remove.
/// </para>
/// <para>
/// Registered declaratively from <c>nlog.config</c> via
/// <c>&lt;extensions&gt;&lt;add assembly="Meshmakers.Octo.Services.Observability" /&gt;</c>.
/// That is deliberate: every service parses its <c>nlog.config</c> in <c>Program.cs</c> through
/// <c>LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile(...)</c> before anything
/// else runs, so a renderer registered in code would have to be registered before that call in
/// twelve separate entry points. Naming the assembly in the file that uses it has no ordering
/// problem.
/// </para>
/// <para>
/// Deliberately NOT marked <c>[ThreadAgnostic]</c>. <see cref="Activity.Current" /> is
/// async-local state; telling NLog the value is context-free would let an async target render
/// it on a pool thread that has no activity, which yields an empty id instead of an error.
/// </para>
/// </remarks>
[LayoutRenderer("otel-trace-id")]
public sealed class OtelTraceIdLayoutRenderer : LayoutRenderer
{
    protected override void Append(StringBuilder builder, LogEventInfo logEvent)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            builder.Append(activity.TraceId.ToHexString());
        }
    }
}

/// <summary>
/// Renders the span id of the ambient <see cref="Activity" /> as 16 lowercase hex characters,
/// or nothing when no activity is current.
/// </summary>
/// <remarks>See <see cref="OtelTraceIdLayoutRenderer" /> for why this lives here.</remarks>
[LayoutRenderer("otel-span-id")]
public sealed class OtelSpanIdLayoutRenderer : LayoutRenderer
{
    protected override void Append(StringBuilder builder, LogEventInfo logEvent)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            builder.Append(activity.SpanId.ToHexString());
        }
    }
}
