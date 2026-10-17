namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Marker of all typed method handlers. Implement <see cref="ICkMethodHandler{TParameters, TResult}" /> or
///     <see cref="ICkMethodHandler{TParameters}" />, not this interface.
/// </summary>
public interface ICkMethodHandler;

/// <summary>
///     Handler of a method that declares a result. The host deserializes the parameters, calls the handler in a new DI
///     scope, serializes the result and maps errors to the response envelope. Handlers are transient per invocation: they
///     may take scoped services (repositories, units of work) through the constructor.
/// </summary>
/// <typeparam name="TParameters">The generated <c>{Type}{Method}Parameters</c> record.</typeparam>
/// <typeparam name="TResult">The generated <c>{Type}{Method}Result</c> record.</typeparam>
public interface ICkMethodHandler<TParameters, TResult> : ICkMethodHandler
    where TParameters : class
    where TResult : class
{
    /// <summary>
    ///     Runs the method. Return the result, or a declared error with <see cref="CkMethodOutcome{TResult}.Fail(string, ValueTuple{string, string}[])" />.
    ///     <paramref name="cancellationToken" /> is cancelled at the deadline of the invocation.
    /// </summary>
    Task<CkMethodOutcome<TResult>> HandleAsync(CkMethodContext<TParameters> context,
        CancellationToken cancellationToken);
}

/// <summary>Handler of a method without a result.</summary>
/// <typeparam name="TParameters">The generated <c>{Type}{Method}Parameters</c> record.</typeparam>
public interface ICkMethodHandler<TParameters> : ICkMethodHandler
    where TParameters : class
{
    /// <summary>
    ///     Runs the method. Return <see cref="CkMethodOutcome.Ok" /> or a declared error.
    ///     <paramref name="cancellationToken" /> is cancelled at the deadline of the invocation.
    /// </summary>
    Task<CkMethodOutcome> HandleAsync(CkMethodContext<TParameters> context, CancellationToken cancellationToken);
}
