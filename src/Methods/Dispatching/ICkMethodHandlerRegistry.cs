namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     In-memory view of the remote CK method handler instances a dispatcher knows, fed by the
///     <c>CkMethodHandlerAnnounced</c> / <c>CkMethodHandlerWithdrawn</c> broadcasts. It is <b>advisory</b>: the mandatory
///     send of the dispatcher decides whether a handler is reachable. The registry adds the contract version and tenant
///     checks, the reason of a refusal and the data for diagnostics (F3.2).
/// </summary>
public interface ICkMethodHandlerRegistry
{
    /// <summary>
    ///     The live handler instances of a method (announced, not withdrawn, not expired), most recently seen first.
    /// </summary>
    /// <param name="qualifiedMethodId">Qualified method id (matched model-version-less and case-insensitively).</param>
    IReadOnlyList<CkMethodHandlerEntry> GetLiveHandlers(string qualifiedMethodId);

    /// <summary>
    ///     The most recently seen instance of the method including expired and withdrawn ones (kept 24 h), or null.
    /// </summary>
    /// <param name="qualifiedMethodId">Qualified method id.</param>
    CkMethodHandlerEntry? GetLastSeen(string qualifiedMethodId);

    /// <summary>All entries (diagnostics).</summary>
    IReadOnlyCollection<CkMethodHandlerEntry> Snapshot();
}

/// <summary>A handler instance for one method as known to the registry.</summary>
/// <param name="MethodId">Qualified method id as announced.</param>
/// <param name="ServiceName">Handler service name.</param>
/// <param name="InstanceId">Handler instance id.</param>
/// <param name="Version">Informational handler service version.</param>
/// <param name="ContractVersion">Highest wire contract version the instance understands.</param>
/// <param name="LastSeenUtc">Last announcement or withdrawal received (dispatcher clock).</param>
/// <param name="ExpiresUtc">The entry is live until then (receipt time plus the announced TTL).</param>
/// <param name="Withdrawn"><c>true</c> after a graceful withdrawal.</param>
/// <param name="Tenants">Served tenants (<c>"*"</c> = all).</param>
public sealed record CkMethodHandlerEntry(
    string MethodId,
    string ServiceName,
    string InstanceId,
    string? Version,
    int ContractVersion,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc,
    bool Withdrawn,
    IReadOnlyList<string> Tenants)
{
    /// <summary>
    ///     <c>true</c> when the entry was expired because the broker returned a mandatory send (no queue bound), not
    ///     because a heartbeat was missed.
    /// </summary>
    public bool NoQueueBound { get; init; }
}
