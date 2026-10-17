using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Throw from deep inside a handler to answer with a declared error without threading an outcome through the call
///     stack. Equivalent to returning <c>CkMethodOutcome.Fail(...)</c>. Every other exception becomes
///     <c>METHOD_HANDLER_FAILED</c>.
/// </summary>
public sealed class CkMethodException : Exception
{
    /// <summary>Creates the exception; the code is validated like in <see cref="CkMethodFailure" />.</summary>
    public CkMethodException(string code, params (string Name, string Value)[] parameters)
        : this(new CkMethodFailure(code, parameters.ToDictionary(p => p.Name, p => p.Value)))
    {
    }

    /// <summary>Creates the exception from a prepared failure.</summary>
    public CkMethodException(CkMethodFailure failure) : base($"CK method failed with {failure?.Code}")
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    /// <summary>The declared error to answer with.</summary>
    public CkMethodFailure Failure { get; }
}
