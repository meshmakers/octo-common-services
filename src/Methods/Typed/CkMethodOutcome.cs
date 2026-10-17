using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     A declared business error of a method: a stable code plus parameters. There is no message text: clients map
///     <c>code</c> and <c>parameters</c> to text in their own language. Parameter values must not contain method
///     parameter values (passwords, tokens, personal data).
/// </summary>
public sealed record CkMethodFailure
{
    /// <summary>Creates a failure; the code is validated.</summary>
    /// <exception cref="ArgumentException">
    ///     The code is neither a declared-code shape (UPPER_SNAKE_CASE, 3 to 64 characters, not <c>METHOD_</c>) nor a
    ///     platform code of <see cref="CkMethodErrorCodes" />.
    /// </exception>
    public CkMethodFailure(string code, IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyList<CkMethodErrorDetail>? details = null)
    {
        if (!CkMethodErrorCodes.IsValidDeclaredCode(code) && !CkMethodErrorCodes.All.Contains(code))
        {
            throw new ArgumentException(
                $"'{code}' is not a valid method error code: declare it in the CK method (UPPER_SNAKE_CASE, 3 to 64 " +
                "characters, no METHOD_ prefix) or use a platform code of CkMethodErrorCodes.", nameof(code));
        }

        Code = code;
        Parameters = parameters ?? new Dictionary<string, string>();
        Details = details;
    }

    /// <summary>Stable, language-neutral error code.</summary>
    public string Code { get; }

    /// <summary>Parameters the client needs to render the message.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Optional detail errors (e.g. one per violated password rule).</summary>
    public IReadOnlyList<CkMethodErrorDetail>? Details { get; }

    internal CkMethodError ToWire() => new() { Code = Code, Parameters = Parameters, Details = Details };
}

/// <summary>Outcome of a method without a result: success or a declared error.</summary>
public sealed class CkMethodOutcome
{
    private CkMethodOutcome(CkMethodFailure? failure)
    {
        Failure = failure;
    }

    /// <summary>True when the method succeeded.</summary>
    public bool IsSuccess => Failure is null;

    /// <summary>The declared error when <see cref="IsSuccess" /> is false.</summary>
    public CkMethodFailure? Failure { get; }

    /// <summary>The method succeeded.</summary>
    public static CkMethodOutcome Ok() => new(null);

    /// <summary>The method failed with a declared error.</summary>
    public static CkMethodOutcome Fail(string code, params (string Name, string Value)[] parameters) =>
        new(new CkMethodFailure(code, parameters.ToDictionary(p => p.Name, p => p.Value)));

    /// <summary>The method failed with a prepared failure.</summary>
    public static CkMethodOutcome Fail(CkMethodFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new CkMethodOutcome(failure);
    }

    /// <summary>A parameter is missing or invalid (<c>METHOD_VALIDATION_FAILED</c>).</summary>
    public static CkMethodOutcome ValidationFailed(string field, string reason) => Fail(
        new CkMethodFailure(CkMethodErrorCodes.MethodValidationFailed,
            new Dictionary<string, string>
            {
                [CkMethodErrorParameters.Field] = field, [CkMethodErrorParameters.Reason] = reason
            }));

    /// <summary>The target does not exist or is not visible (<c>METHOD_TARGET_NOT_FOUND</c>).</summary>
    public static CkMethodOutcome TargetNotFound() => Fail(CkMethodErrorCodes.MethodTargetNotFound);

    /// <summary>The caller may not invoke the method on this target (<c>METHOD_FORBIDDEN</c>).</summary>
    public static CkMethodOutcome Forbidden(string reason) => Fail(
        new CkMethodFailure(CkMethodErrorCodes.MethodForbidden,
            new Dictionary<string, string> { [CkMethodErrorParameters.Reason] = reason }));
}

/// <summary>
///     Outcome of a method with a result: the result or a declared error. A <typeparamref name="TResult" /> converts
///     implicitly, so <c>return new MyResult { Value = x };</c> is a success.
/// </summary>
/// <typeparam name="TResult">The generated result record (<c>{Type}{Method}Result</c>).</typeparam>
public sealed class CkMethodOutcome<TResult>
    where TResult : class
{
    private CkMethodOutcome(TResult? value, CkMethodFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    /// <summary>True when the method succeeded.</summary>
    public bool IsSuccess => Failure is null;

    /// <summary>The result when <see cref="IsSuccess" /> is true.</summary>
    public TResult? Value { get; }

    /// <summary>The declared error when <see cref="IsSuccess" /> is false.</summary>
    public CkMethodFailure? Failure { get; }

    /// <summary>The method succeeded with a result.</summary>
    public static CkMethodOutcome<TResult> Ok(TResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CkMethodOutcome<TResult>(value, null);
    }

    /// <summary>The method failed with a declared error.</summary>
    public static CkMethodOutcome<TResult> Fail(string code, params (string Name, string Value)[] parameters) =>
        new(null, new CkMethodFailure(code, parameters.ToDictionary(p => p.Name, p => p.Value)));

    /// <summary>The method failed with a prepared failure.</summary>
    public static CkMethodOutcome<TResult> Fail(CkMethodFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new CkMethodOutcome<TResult>(null, failure);
    }

    /// <summary>A parameter is missing or invalid (<c>METHOD_VALIDATION_FAILED</c>).</summary>
    public static CkMethodOutcome<TResult> ValidationFailed(string field, string reason) =>
        Fail(CkMethodOutcome.ValidationFailed(field, reason).Failure!);

    /// <summary>The target does not exist or is not visible (<c>METHOD_TARGET_NOT_FOUND</c>).</summary>
    public static CkMethodOutcome<TResult> TargetNotFound() => Fail(CkMethodOutcome.TargetNotFound().Failure!);

    /// <summary>The caller may not invoke the method on this target (<c>METHOD_FORBIDDEN</c>).</summary>
    public static CkMethodOutcome<TResult> Forbidden(string reason) =>
        Fail(CkMethodOutcome.Forbidden(reason).Failure!);

    /// <summary>A result is a success.</summary>
    public static implicit operator CkMethodOutcome<TResult>(TResult value) => Ok(value);

    /// <summary>A failure is a failed outcome.</summary>
    public static implicit operator CkMethodOutcome<TResult>(CkMethodFailure failure) => Fail(failure);
}
