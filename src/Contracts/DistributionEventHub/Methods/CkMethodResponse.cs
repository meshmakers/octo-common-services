using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Result of a CK method invocation (the response envelope shared by dispatcher, handlers, the in-process transport
///     and the pipeline handler). Errors are data: a handler never faults the request.
/// </summary>
/// <remarks>
///     Exactly one of three shapes:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Succeeded</b>: <see cref="Success" /> is <c>true</c>, <see cref="Operation" /> is <c>null</c>,
///                 <see cref="ResultJson" /> holds the result if the method declares one.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Failed</b>: <see cref="Success" /> is <c>false</c>, <see cref="Error" /> is set.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Accepted</b> (asynchronous method): <see cref="Success" /> is <c>true</c>, <see cref="Operation" />
///                 is set and <see cref="ResultJson" /> is <c>null</c>; the outcome is read through the handle (F3.6).
///             </description>
///         </item>
///     </list>
///     The envelope carries no localized text; clients render <see cref="CkMethodError.Code" /> and
///     <see cref="CkMethodError.Parameters" /> in their own language.
/// </remarks>
public sealed record CkMethodResponse
{
    /// <summary>
    ///     Version of the wire contract the answering side wrote. A missing member reads as <c>1</c>.
    /// </summary>
    public int ContractVersion { get; init; } = CkMethodContract.CurrentVersion;

    /// <summary>
    ///     Id of the invocation this response belongs to (<see cref="CkMethodRequest.InvocationId" />).
    /// </summary>
    public required Guid InvocationId { get; init; }

    /// <summary>
    ///     <c>true</c> when the method executed successfully or was accepted for asynchronous execution.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    ///     The error; <c>null</c> when <see cref="Success" /> is <c>true</c>.
    /// </summary>
    public CkMethodError? Error { get; init; }

    /// <summary>
    ///     Method result as camelCase JSON, if the method declares a result and ran synchronously.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>
    ///     Handle of the running operation of an asynchronous method; <c>null</c> for a synchronous answer.
    /// </summary>
    public CkMethodOperationHandle? Operation { get; init; }

    /// <summary>
    ///     Instance id of the handler that answered; empty when no handler answered (platform outcome).
    /// </summary>
    public required string HandlerInstanceId { get; init; }

    /// <summary>
    ///     <c>true</c> when the method was accepted for asynchronous execution (<see cref="Operation" /> is set).
    /// </summary>
    [JsonIgnore]
    public bool IsAccepted => Success && Operation is not null;

    /// <summary>
    ///     Creates the response of a successful, synchronous call.
    /// </summary>
    /// <param name="invocationId">Id of the invocation</param>
    /// <param name="handlerInstanceId">Instance id of the answering handler</param>
    /// <param name="resultJson">Result as JSON, <c>null</c> for a method without result</param>
    public static CkMethodResponse Succeeded(Guid invocationId, string handlerInstanceId, string? resultJson = null) =>
        new() { InvocationId = invocationId, HandlerInstanceId = handlerInstanceId, Success = true, ResultJson = resultJson };

    /// <summary>
    ///     Creates the response of a method accepted for asynchronous execution.
    /// </summary>
    /// <param name="invocationId">Id of the invocation</param>
    /// <param name="handlerInstanceId">Instance id of the answering handler</param>
    /// <param name="operation">Handle to follow the operation</param>
    public static CkMethodResponse Accepted(Guid invocationId, string handlerInstanceId,
        CkMethodOperationHandle operation) =>
        new() { InvocationId = invocationId, HandlerInstanceId = handlerInstanceId, Success = true, Operation = operation };

    /// <summary>
    ///     Creates the response of a failed call.
    /// </summary>
    /// <param name="invocationId">Id of the invocation</param>
    /// <param name="handlerInstanceId">Instance id of the answering handler, empty for platform outcomes</param>
    /// <param name="error">The error</param>
    public static CkMethodResponse Failed(Guid invocationId, string handlerInstanceId, CkMethodError error) =>
        new() { InvocationId = invocationId, HandlerInstanceId = handlerInstanceId, Success = false, Error = error };
}

/// <summary>
///     Language-neutral error of a CK method call: a stable <see cref="Code" /> and string
///     <see cref="Parameters" />. There is deliberately no message text on the wire.
/// </summary>
public sealed record CkMethodError
{
    /// <summary>
    ///     Stable code: a platform code (<see cref="CkMethodErrorCodes" />, prefix <c>METHOD_</c>) or an error code the
    ///     method declares in its CK model (never with the reserved prefix).
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    ///     Parameters of the error (well-known names: <see cref="CkMethodErrorParameters" />). Values are plain strings
    ///     and never contain method parameter values (they may be passwords).
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>
    ///     Optional detail entries, e.g. one per violated password policy.
    /// </summary>
    public IReadOnlyList<CkMethodErrorDetail>? Details { get; init; }

    /// <summary>
    ///     Creates an error.
    /// </summary>
    /// <param name="code">Error code</param>
    /// <param name="parameters">Name/value pairs</param>
    public static CkMethodError Create(string code, params (string Name, string Value)[] parameters) =>
        new() { Code = code, Parameters = parameters.ToDictionary(p => p.Name, p => p.Value) };
}

/// <summary>
///     Detail entry of a <see cref="CkMethodError" />.
/// </summary>
public sealed record CkMethodErrorDetail
{
    /// <summary>
    ///     Detail code, e.g. an Identity error code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    ///     Parameters of the detail (same rules as <see cref="CkMethodError.Parameters" />).
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
///     Handle of a running asynchronous operation (the polling surface itself belongs to F3.6).
/// </summary>
public sealed record CkMethodOperationHandle
{
    /// <summary>
    ///     Opaque id of the operation, unique in the tenant. Clients treat it as a string.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    ///     Time (UTC) the operation was accepted.
    /// </summary>
    public DateTime? AcceptedAtUtc { get; init; }

    /// <summary>
    ///     Suggested delay before the first status check, in seconds.
    /// </summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    ///     Time (UTC) after which the platform may forget the operation result.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; init; }
}
