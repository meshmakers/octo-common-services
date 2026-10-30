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

    /// <summary>
    ///     Where the row came from (AB#7836). <see cref="CkMethodHandlerEntrySource.Snapshot" /> from a restore of the
    ///     persisted snapshot until an announcement, a discovery answer or a withdrawal of the same instance replaces it.
    ///     Not persisted: the source is derived at restore time.
    /// </summary>
    public CkMethodHandlerEntrySource Source { get; init; } = CkMethodHandlerEntrySource.Announcement;

    /// <summary>
    ///     The state of the entry at <paramref name="nowUtc" />, by the same rules as
    ///     <see cref="ICkMethodHandlerRegistry.GetLiveHandlers" />: <see cref="CkMethodHandlerState.Withdrawn" /> after a
    ///     withdrawal, <see cref="CkMethodHandlerState.Live" /> while not expired, otherwise
    ///     <see cref="CkMethodHandlerState.NoQueueBound" /> (the broker refused a send) or
    ///     <see cref="CkMethodHandlerState.Expired" /> (heartbeat missed). A restored row follows the same rules, so it is
    ///     never live after its <see cref="ExpiresUtc" />.
    /// </summary>
    /// <param name="nowUtc">The time to evaluate at (dispatcher clock).</param>
    public CkMethodHandlerState GetState(DateTime nowUtc)
    {
        if (Withdrawn)
        {
            return CkMethodHandlerState.Withdrawn;
        }

        if (ExpiresUtc > nowUtc)
        {
            return CkMethodHandlerState.Live;
        }

        return NoQueueBound ? CkMethodHandlerState.NoQueueBound : CkMethodHandlerState.Expired;
    }
}

/// <summary>Where a registry row came from.</summary>
public enum CkMethodHandlerEntrySource
{
    /// <summary>Received as an announcement (heartbeat or discovery answer) or a withdrawal.</summary>
    Announcement = 0,

    /// <summary>Restored from the persisted snapshot (AB#5671) and not yet confirmed by the handler.</summary>
    Snapshot = 1
}

/// <summary>State of a registry row at a point in time.</summary>
public enum CkMethodHandlerState
{
    /// <summary>Announced, not withdrawn, not expired.</summary>
    Live = 0,

    /// <summary>The announced TTL ran out without a heartbeat.</summary>
    Expired = 1,

    /// <summary>The handler withdrew gracefully.</summary>
    Withdrawn = 2,

    /// <summary>Expired because the broker returned a mandatory send (no queue bound).</summary>
    NoQueueBound = 3
}
