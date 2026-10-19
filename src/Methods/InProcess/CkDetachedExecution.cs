using System.Diagnostics;
using System.Globalization;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>Telemetry names of the CK method host.</summary>
public static class CkMethodTelemetry
{
    /// <summary>
    ///     Name of the <see cref="ActivitySource" /> of the in-process handler span. Subscribe to it (<c>AddSource</c>) to
    ///     export the span; without a listener no span exists, but the caller's trace still continues inside the handler.
    /// </summary>
    public const string ActivitySourceName = "Meshmakers.Octo.Methods";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

/// <summary>
///     Starts work in a <b>detached</b> execution flow (OD-8): the work does not inherit the caller's
///     <see cref="ExecutionContext" />, so no <c>AsyncLocal</c> value of the caller (the request session, the principal
///     scope, a logging scope, ...) is visible in it, also not after its first <c>await</c>. Exactly four things are
///     carried across the hop, explicitly and by value: the trace (the caller's <see cref="Activity" /> becomes the parent
///     of the handler span), <see cref="CultureInfo.CurrentCulture" /> and <see cref="CultureInfo.CurrentUICulture" />.
///     The call-identifying logging scope is opened by the invoker inside the detached flow, the cancellation token is
///     passed as an argument. Everything else is dropped on purpose.
/// </summary>
internal static class CkDetachedExecution
{
    public static Task<T> Run<T>(string methodId, string transport, Func<Task<T>> work)
    {
        var parent = Activity.Current;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        async Task<T> Detached()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            // The trace continues even when nobody listens to our source: the ambient activity of this flow is the
            // caller's one, so spans the handler starts (HTTP, database) still hang under the caller's trace.
            Activity.Current = parent;
            using var span = CkMethodTelemetry.ActivitySource.StartActivity("CK method handle");
            span?.SetTag("ck.method.id", methodId);
            span?.SetTag("ck.method.transport", transport);
            return await work().ConfigureAwait(false);
        }

        // With the flow suppressed, Task.Run queues the work without capturing the caller's ExecutionContext. The
        // AsyncFlowControl must be disposed on this thread, so the suppression stays in this synchronous method.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(Detached);
        }
    }
}
