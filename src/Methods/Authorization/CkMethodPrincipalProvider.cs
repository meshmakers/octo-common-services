namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     Supplies the authenticated caller for the call that the pipeline is running. The default implementation
///     (<see cref="CkMethodPrincipalScope" />) hands over the principal that the process which started the call
///     established; replace it where the principal comes from elsewhere. Return <c>null</c> when there is none: the
///     authorization filter then refuses the call.
/// </summary>
public interface ICkMethodPrincipalProvider
{
    /// <summary>The caller of <paramref name="context" />, or <c>null</c> when the call is not authenticated.</summary>
    CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context);
}

/// <summary>
///     Ambient hand-over of the caller to the invocation pipeline. The gateway (after it validated the token of the
///     request) and a job (with its own identity) open a scope around the call:
///     <code>using (CkMethodPrincipalScope.Begin(principal)) { await dispatcher.InvokeAsync(request); }</code>
///     The scope flows with the async call chain, so the in-process and the bus transport both see it; it is not part
///     of the request and never travels over the bus.
/// </summary>
public sealed class CkMethodPrincipalScope : ICkMethodPrincipalProvider
{
    private static readonly AsyncLocal<CkMethodPrincipal?> Current = new();

    /// <summary>Stateless instance for code that has no provider registered.</summary>
    internal static CkMethodPrincipalScope Instance { get; } = new();

    /// <summary>The principal of the current async flow, or <c>null</c>.</summary>
    public static CkMethodPrincipal? CurrentPrincipal => Current.Value;

    /// <summary>
    ///     Makes <paramref name="principal" /> the caller of every method call started inside the returned scope.
    ///     Dispose the scope to restore the previous principal.
    /// </summary>
    public static IDisposable Begin(CkMethodPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var previous = Current.Value;
        Current.Value = principal;
        return new Restore(previous);
    }

    /// <inheritdoc />
    public CkMethodPrincipal? GetPrincipal(CkMethodInvocationContext context) => Current.Value;

    private sealed class Restore(CkMethodPrincipal? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }
}
