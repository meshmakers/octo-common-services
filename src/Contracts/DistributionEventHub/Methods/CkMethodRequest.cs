using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Invocation of a CK method, sent by the dispatcher (asset-repo) to the method queue
///     (<see cref="CkMethodQueueNames.For" />) and answered with a <see cref="CkMethodResponse" />.
/// </summary>
/// <remarks>
///     <see cref="ParametersJson" /> and <see cref="CallerAccessToken" /> carry sensitive values (passwords, JWTs).
///     They must never be logged; <see cref="ToString" /> is overridden to redact them.
/// </remarks>
public sealed record CkMethodRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant the method is invoked in</param>
    public CkMethodRequest(string tenantId) : base(tenantId)
    {
    }

    /// <summary>
    ///     Version of the wire contract the dispatcher wrote (<see cref="CkMethodContract.CurrentVersion" />).
    ///     A missing member reads as <c>1</c>. A handler that does not understand the version answers
    ///     <see cref="CkMethodErrorCodes.MethodContractVersionUnsupported" /> without executing the method.
    /// </summary>
    public int ContractVersion { get; init; } = CkMethodContract.CurrentVersion;

    /// <summary>
    ///     Unique id of this invocation (correlates dispatcher, handler, logs and audit).
    /// </summary>
    public required Guid InvocationId { get; init; }

    /// <summary>
    ///     Id that ties this invocation to the request that caused it (for example the id of the GraphQL request or of a
    ///     pipeline execution); <c>null</c> when the invocation is its own root. Handlers copy it into logs and into
    ///     <see cref="CkMethodErrorParameters.CorrelationId" /> of failures; it carries no meaning for the handler logic.
    /// </summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>
    ///     Qualified, model-version-less method id, e.g. <c>System.Identity/User.ChangePassword-1</c>.
    /// </summary>
    public required string MethodId { get; init; }

    /// <summary>
    ///     Concrete CK type id of the target entity, e.g. <c>System.Identity/User</c>.
    /// </summary>
    public required string CkTypeId { get; init; }

    /// <summary>
    ///     Runtime id of the target entity; <c>null</c> for static methods.
    /// </summary>
    public string? TargetRtId { get; init; }

    /// <summary>
    ///     Method parameters as a camelCase JSON object. CONTAINS SENSITIVE VALUES — never log.
    /// </summary>
    public string ParametersJson { get; init; } = "{}";

    /// <summary>
    ///     Informational description of the caller. Handlers MUST NOT base decisions on it (the bus is not
    ///     authenticated); they re-validate <see cref="CallerAccessToken" /> instead.
    /// </summary>
    public CkMethodCaller? Caller { get; init; }

    /// <summary>
    ///     Raw access token (JWT) of the caller for authoritative re-validation by the handler. Never log.
    /// </summary>
    public string? CallerAccessToken { get; init; }

    /// <summary>
    ///     Absolute deadline (UTC). A handler receiving the request after the deadline answers
    ///     <see cref="CkMethodErrorCodes.MethodTimeout" /> without executing it.
    /// </summary>
    public required DateTime DeadlineUtc { get; init; }

    /// <summary>
    ///     Client-chosen key that makes a retry of a non-idempotent method safe (processed by F3.5; until then handlers
    ///     ignore it). Opaque, at most 128 characters.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    ///     Redacting string representation (the record is sealed, so no derived record can regenerate a
    ///     <c>PrintMembers</c> that prints the sensitive members): never contains <see cref="ParametersJson" />,
    ///     <see cref="CallerAccessToken" /> or <see cref="Caller" />.
    /// </summary>
    public override string ToString() => $"CkMethodRequest {{ {MethodId} {TenantId}/{TargetRtId} {InvocationId} }}";
}
