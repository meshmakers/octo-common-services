using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Tripwire of the in-process transport (OD-8). The transport runs every handler detached from the ambient execution
///     context of its caller; a host registers a probe for each kind of request-bound ambient state it owns (for example
///     the asset-repo request session). The probe is asked <b>at handler entry</b>, in the handler's own execution flow and
///     DI scope, before the handler is created. When any probe reports that state, the call fails with
///     <c>METHOD_HANDLER_FAILED</c> and the handler does not run: detaching is supposed to make the answer always
///     <c>false</c>, so <c>true</c> means a regression (somebody reintroduced an ambient value or bypassed the detach).
///     Register with <c>AddCkInProcessAmbientStateProbe&lt;TProbe&gt;()</c>. A probe is a singleton, must be thread-safe,
///     cheap and must not throw (a throwing probe also fails the call: fail closed). It answers yes/no; it never logs or
///     returns the data it looked at.
/// </summary>
public interface ICkInProcessAmbientStateProbe
{
    /// <summary>
    ///     True when request-bound ambient state (a request session, a request principal, ...) is visible in the
    ///     current execution flow.
    /// </summary>
    /// <param name="handlerServices">The new DI scope of this invocation, for probes that need a scoped accessor.</param>
    bool IsRequestStateVisible(IServiceProvider handlerServices);
}

/// <summary>The violation found by the entry guard: which probe, and whether it threw instead of answering.</summary>
internal sealed record CkMethodEntryViolation(Type ProbeType, Exception? Fault);

/// <summary>Check that runs in the handler's flow before the handler is resolved (in-process transport only).</summary>
internal interface ICkMethodEntryGuard
{
    CkMethodEntryViolation? FindViolation(IServiceProvider handlerServices);
}

internal sealed class CkInProcessEntryGuard(IEnumerable<ICkInProcessAmbientStateProbe> probes) : ICkMethodEntryGuard
{
    private readonly ICkInProcessAmbientStateProbe[] _probes = probes.ToArray();

    public CkMethodEntryViolation? FindViolation(IServiceProvider handlerServices)
    {
        foreach (var probe in _probes)
        {
            try
            {
                if (probe.IsRequestStateVisible(handlerServices))
                {
                    return new CkMethodEntryViolation(probe.GetType(), null);
                }
            }
            catch (Exception e)
            {
                return new CkMethodEntryViolation(probe.GetType(), e);
            }
        }

        return null;
    }
}
