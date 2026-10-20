using System.Text.Json;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>Result of one typed handler call, already serialized for the wire.</summary>
internal readonly record struct CkMethodAdapterOutcome(CkMethodFailure? Failure, string? ResultJson);

/// <summary>Bridges the untyped host to a typed handler without reflection per call.</summary>
internal abstract class CkMethodHandlerAdapter
{
    public abstract Task<CkMethodAdapterOutcome> InvokeAsync(object handler, CkMethodRequest request,
        object parameters, TimeProvider timeProvider, CkMethodPrincipal? callerPrincipal,
        CancellationToken cancellationToken);
}

internal sealed class CkMethodHandlerAdapter<THandler, TParameters, TResult> : CkMethodHandlerAdapter
    where THandler : class, ICkMethodHandler<TParameters, TResult>
    where TParameters : class
    where TResult : class
{
    public override async Task<CkMethodAdapterOutcome> InvokeAsync(object handler, CkMethodRequest request,
        object parameters, TimeProvider timeProvider, CkMethodPrincipal? callerPrincipal,
        CancellationToken cancellationToken)
    {
        var context = new CkMethodContext<TParameters>(request, (TParameters)parameters, timeProvider)
        {
            CallerPrincipal = callerPrincipal
        };
        var outcome = await ((THandler)handler).HandleAsync(context, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The handler returned a null outcome.");
        return outcome.IsSuccess
            ? new CkMethodAdapterOutcome(null, JsonSerializer.Serialize(outcome.Value, CkMethodJson.Options))
            : new CkMethodAdapterOutcome(outcome.Failure, null);
    }
}

internal sealed class CkMethodHandlerAdapter<THandler, TParameters> : CkMethodHandlerAdapter
    where THandler : class, ICkMethodHandler<TParameters>
    where TParameters : class
{
    public override async Task<CkMethodAdapterOutcome> InvokeAsync(object handler, CkMethodRequest request,
        object parameters, TimeProvider timeProvider, CkMethodPrincipal? callerPrincipal,
        CancellationToken cancellationToken)
    {
        var context = new CkMethodContext<TParameters>(request, (TParameters)parameters, timeProvider)
        {
            CallerPrincipal = callerPrincipal
        };
        var outcome = await ((THandler)handler).HandleAsync(context, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The handler returned a null outcome.");
        return new CkMethodAdapterOutcome(outcome.Failure, null);
    }
}
