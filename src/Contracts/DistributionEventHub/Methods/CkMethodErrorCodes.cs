using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Platform error codes of CK method invocations, shared by dispatcher (gateway) and handlers. This class is the single
///     source of the list: the constants, <see cref="Catalog" /> (documentation per code) and the table in
///     <c>docs/method-wire-contract.md</c> are kept in sync by tests.
/// </summary>
/// <remarks>
///     The prefix <see cref="PlatformPrefix" /> is reserved for the platform (CK compiler message 103): a method declares
///     its own codes without it. New platform codes are additive and need a new catalog entry and documentation row.
/// </remarks>
public static partial class CkMethodErrorCodes
{
    /// <summary>
    ///     Reserved prefix of all platform codes.
    /// </summary>
    public const string PlatformPrefix = "METHOD_";

    /// <summary>
    ///     No live handler is registered for the method/tenant, or the broker returned the request (no queue bound).
    /// </summary>
    public const string MethodHandlerNotAvailable = "METHOD_HANDLER_NOT_AVAILABLE";

    /// <summary>
    ///     The handler did not answer within the timeout, or received the request after its deadline.
    ///     From the dispatcher this means <b>outcome unknown</b>: the handler may have completed the method —
    ///     clients must not retry a non-idempotent method blindly.
    /// </summary>
    public const string MethodTimeout = "METHOD_TIMEOUT";

    /// <summary>
    ///     The caller is not allowed to invoke the method on the target.
    /// </summary>
    public const string MethodForbidden = "METHOD_FORBIDDEN";

    /// <summary>
    ///     The parameters are invalid (missing required parameter, wrong type, ...).
    /// </summary>
    public const string MethodValidationFailed = "METHOD_VALIDATION_FAILED";

    /// <summary>
    ///     The target entity does not exist or is not visible to the caller.
    /// </summary>
    public const string MethodTargetNotFound = "METHOD_TARGET_NOT_FOUND";

    /// <summary>
    ///     The handler failed unexpectedly (exception, fault, infrastructure error).
    /// </summary>
    public const string MethodHandlerFailed = "METHOD_HANDLER_FAILED";

    /// <summary>
    ///     The handler received an invocation that may already have been executed (broker redelivery after a lost
    ///     acknowledgement, or a duplicate invocation id) and did not execute it again. The method may or may not
    ///     have taken effect; clients must check the state before retrying.
    /// </summary>
    public const string MethodOutcomeUnknown = "METHOD_OUTCOME_UNKNOWN";

    /// <summary>
    ///     Reserved: the registered handler version does not support the requested method version.
    /// </summary>
    public const string MethodNotSupportedByHandlerVersion = "METHOD_NOT_SUPPORTED_BY_HANDLER_VERSION";

    /// <summary>
    ///     The message carries a wire contract version newer than the receiver understands
    ///     (<see cref="CkMethodContract.IsSupported" />). The method was not executed.
    /// </summary>
    public const string MethodContractVersionUnsupported = "METHOD_CONTRACT_VERSION_UNSUPPORTED";

    /// <summary>
    ///     The parameters of the call (<see cref="CkMethodRequest.ParametersJson" />) are larger than the limit of the
    ///     service (<c>Methods:MaxParametersBytes</c>, default 1 MiB). The method was not executed.
    /// </summary>
    public const string MethodParametersTooLarge = "METHOD_PARAMETERS_TOO_LARGE";

    /// <summary>
    ///     A call with the same idempotency key (same tenant, method and caller) is still running; this call was not
    ///     executed. Send the same key again later: the first result is then replayed (OD-4 row 3).
    /// </summary>
    public const string MethodRequestInProgress = "METHOD_REQUEST_IN_PROGRESS";

    /// <summary>
    ///     Documentation of every platform code: meaning, whether the method ran, the parameters the code carries and
    ///     what a caller should do. The single source for the documentation table.
    /// </summary>
    public static IReadOnlyList<CkMethodErrorCodeInfo> Catalog { get; } =
    [
        new(MethodHandlerNotAvailable, CkMethodExecutionState.NotExecuted,
            "No live handler for the method (not announced, expired, or the pipeline is not deployed).",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.Reason],
            "Retry later; the handler service may be starting or restarting."),
        new(MethodTimeout, CkMethodExecutionState.Unknown,
            "The handler did not answer before the deadline, or the request reached it after the deadline.",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.TimeoutSeconds],
            "Do not retry a non-idempotent method blindly; check the state first or use an idempotency key."),
        new(MethodForbidden, CkMethodExecutionState.NotExecuted,
            "The caller may not invoke the method on this target.",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.Reason],
            "Do not retry; obtain the missing role or permission."),
        new(MethodValidationFailed, CkMethodExecutionState.NotExecuted,
            "A method parameter is missing or invalid.",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.Field, CkMethodErrorParameters.Reason],
            "Fix the parameter named by 'field' and call again."),
        new(MethodTargetNotFound, CkMethodExecutionState.NotExecuted,
            "The target entity does not exist or is not visible to the caller.",
            [CkMethodErrorParameters.MethodId],
            "Check the target id; do not retry unchanged."),
        new(MethodHandlerFailed, CkMethodExecutionState.Failed,
            "The handler failed unexpectedly. The details are in the handler log under the correlation id.",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.CorrelationId],
            "Report the correlation id to an operator; retry only if the method is idempotent."),
        new(MethodOutcomeUnknown, CkMethodExecutionState.Unknown,
            "A possible duplicate delivery was detected and not executed again; the first run may or may not have taken effect.",
            [CkMethodErrorParameters.MethodId],
            "Check the state of the target before calling again."),
        new(MethodNotSupportedByHandlerVersion, CkMethodExecutionState.NotExecuted,
            "Reserved: the handler does not support the requested method version.",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.SupportedVersion],
            "Wait for the handler rollout to finish."),
        new(MethodContractVersionUnsupported, CkMethodExecutionState.NotExecuted,
            "The message uses a newer wire contract version than the receiver understands.",
            [CkMethodErrorParameters.RequestedVersion, CkMethodErrorParameters.SupportedVersion],
            "Wait for the rolling update to finish, then retry."),
        new(MethodParametersTooLarge, CkMethodExecutionState.NotExecuted,
            "The parameters are larger than the limit of the service (UTF-8 bytes of the parameter JSON, option Methods:MaxParametersBytes, default 1 MiB).",
            [CkMethodErrorParameters.MethodId, CkMethodErrorParameters.MaxBytes, CkMethodErrorParameters.ActualBytes],
            "Do not retry unchanged; send fewer or smaller parameters (large content belongs in a file, not in a parameter)."),
        new(MethodRequestInProgress, CkMethodExecutionState.NotExecuted,
            "A call with the same idempotency key (same tenant, method and caller) is still running. This call was not executed.",
            [CkMethodErrorParameters.MethodId],
            "Send the same idempotency key again later; the result of the first call is then replayed.")
    ];

    /// <summary>
    ///     All platform codes.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [.. Catalog.Select(c => c.Code)];

    /// <summary>
    ///     Returns whether <paramref name="code" /> uses the reserved platform prefix.
    /// </summary>
    /// <param name="code">Error code</param>
    public static bool IsPlatformCode(string code) =>
        code.StartsWith(PlatformPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Returns whether <paramref name="code" /> is a valid code a method may declare: UPPER_SNAKE_CASE of 3 to 64
    ///     characters, not using the reserved <see cref="PlatformPrefix" />.
    /// </summary>
    /// <param name="code">Error code</param>
    public static bool IsValidDeclaredCode(string code) =>
        code.Length is >= 3 and <= 64 && !IsPlatformCode(code) && DeclaredCodeRegex().IsMatch(code);

    [GeneratedRegex("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$")]
    private static partial Regex DeclaredCodeRegex();
}

/// <summary>
///     Whether the method body ran when an error with a given code is returned.
/// </summary>
public enum CkMethodExecutionState
{
    /// <summary>
    ///     The method did not run; the call can be repeated after the cause is removed.
    /// </summary>
    NotExecuted,

    /// <summary>
    ///     The method may or may not have run.
    /// </summary>
    Unknown,

    /// <summary>
    ///     The method ran and failed.
    /// </summary>
    Failed
}

/// <summary>
///     Documentation of one platform error code.
/// </summary>
/// <param name="Code">The code</param>
/// <param name="Execution">Whether the method ran</param>
/// <param name="Description">Meaning (English, for documentation only — never sent on the wire)</param>
/// <param name="ParameterNames">Names of the parameters the code carries (a subset may be present)</param>
/// <param name="CallerAction">What the caller should do</param>
public sealed record CkMethodErrorCodeInfo(
    string Code,
    CkMethodExecutionState Execution,
    string Description,
    IReadOnlyList<string> ParameterNames,
    string CallerAction);

/// <summary>
///     Well-known names of <see cref="CkMethodError.Parameters" />. Names are camelCase; adding names is additive.
/// </summary>
public static class CkMethodErrorParameters
{
    /// <summary>Qualified method id.</summary>
    public const string MethodId = "methodId";

    /// <summary>Id correlating the failure with the handler log (<see cref="CkMethodRequest.CorrelationId" /> or the invocation id).</summary>
    public const string CorrelationId = "correlationId";

    /// <summary>Name of an invalid method parameter.</summary>
    public const string Field = "field";

    /// <summary>Short machine-readable reason, e.g. <c>pipeline-not-deployed</c>, <c>no-live-handler</c>.</summary>
    public const string Reason = "reason";

    /// <summary>The timeout that applied, in seconds.</summary>
    public const string TimeoutSeconds = "timeoutSeconds";

    /// <summary>Contract version the message carried.</summary>
    public const string RequestedVersion = "requestedVersion";

    /// <summary>Highest version the receiver supports.</summary>
    public const string SupportedVersion = "supportedVersion";

    /// <summary>The limit for the parameters, in UTF-8 bytes.</summary>
    public const string MaxBytes = "maxBytes";

    /// <summary>The UTF-8 byte length of the parameters that were refused.</summary>
    public const string ActualBytes = "actualBytes";
}
