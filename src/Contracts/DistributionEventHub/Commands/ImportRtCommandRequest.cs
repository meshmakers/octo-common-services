using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

/// <summary>
///     Arguments for import runtime data
/// </summary>
public record ImportRtCommandRequest : CommandBaseRequest
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="tenantId">Tenant ID</param>
    /// <param name="importStrategy">Import strategy to use</param>
    /// <param name="cacheFileKey">Cache file key</param>
    public ImportRtCommandRequest(string tenantId, ImportStrategy importStrategy, string cacheFileKey)
        : base(tenantId)
    {
        ImportStrategy = importStrategy;
        CacheFileKey = cacheFileKey;
    }

    /// <summary>
    ///     Returns the import strategy to use
    /// </summary>
    public ImportStrategy ImportStrategy { get; }

    /// <summary>
    ///     Returns the cache file key
    /// </summary>
    public string CacheFileKey { get; }

    /// <summary>
    ///     Subject id (user <c>sub</c> claim or client id) of the caller that started the import through the API
    ///     (AB#6392). Lets the consumer apply the blueprint-lock protection of opted-in CK types on behalf of that
    ///     caller. Optional and additive: a message from an older producer carries none, and the consumer protects
    ///     nevertheless (a missing caller is never treated as system). An older consumer ignores the field.
    /// </summary>
    public string? InitiatedBySubjectId { get; init; }

    /// <summary>
    ///     AB#8705: the kind of principal (signed-in person or service client) that started the import through the API,
    ///     derived by the producer from the caller's token. The consumer passes it to the engine
    ///     (<see cref="RtImportCallerContext.PrincipalKind" />), where types with a <c>writeAccess</c> block admit or
    ///     refuse the import by it (AB#8574). Optional and additive: a message from an older producer carries none and
    ///     reads as <see cref="RtPrincipalKind.Unknown" />, which restricted types refuse (fail closed) and every other
    ///     type ignores. An older consumer ignores the field.
    /// </summary>
    public RtPrincipalKind InitiatedByPrincipalKind { get; init; } = RtPrincipalKind.Unknown;
}
