using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Broadcast by a CK method handler instance at start, on every heartbeat and in reply to
///     <see cref="CkMethodHandlerDiscoveryRequested" />. Dispatchers keep their handler registry from it.
/// </summary>
/// <param name="CorrelationId">Correlation id</param>
/// <param name="Timestamp">Time (UTC) the announcement was created; the registry entry expires at Timestamp + TtlSeconds</param>
public record CkMethodHandlerAnnounced(Guid CorrelationId, DateTime Timestamp) : EventBase(CorrelationId, Timestamp)
{
    /// <summary>
    ///     Highest wire contract version the handler instance understands (<see cref="CkMethodContract.CurrentVersion" />
    ///     of its library). Dispatchers never send a newer <see cref="CkMethodRequest.ContractVersion" />.
    /// </summary>
    public int ContractVersion { get; init; } = CkMethodContract.CurrentVersion;

    /// <summary>
    ///     Name of the handler service, e.g. <c>IdentityService</c>.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    ///     Instance id (Guid "N") generated once per handler process.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    ///     Qualified method ids handled by this instance.
    /// </summary>
    public required IReadOnlyList<string> MethodIds { get; init; }

    /// <summary>
    ///     Tenants served; <c>"*"</c> means all tenants (always <c>"*"</c> until per-tenant handlers exist).
    /// </summary>
    public IReadOnlyList<string> Tenants { get; init; } = [CkMethodHandlerTenants.All];

    /// <summary>
    ///     Informational version of the handler service.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    ///     Interval of the heartbeat announcements.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 15;

    /// <summary>
    ///     Time after <see cref="EventBase.Timestamp" /> at which a dispatcher considers the instance gone.
    /// </summary>
    public int TtlSeconds { get; init; } = 45;
}

/// <summary>
///     Broadcast by a CK method handler instance on graceful shutdown, before its method endpoints are removed.
/// </summary>
/// <param name="CorrelationId">Correlation id</param>
/// <param name="Timestamp">Time (UTC) of the withdrawal</param>
public record CkMethodHandlerWithdrawn(Guid CorrelationId, DateTime Timestamp) : EventBase(CorrelationId, Timestamp)
{
    /// <summary>
    ///     Name of the handler service.
    /// </summary>
    public required string ServiceName { get; init; }

    /// <summary>
    ///     Instance id of the withdrawing handler process.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    ///     Qualified method ids no longer handled by this instance.
    /// </summary>
    public required IReadOnlyList<string> MethodIds { get; init; }
}

/// <summary>
///     Broadcast by a dispatcher at start; every handler instance answers immediately with
///     <see cref="CkMethodHandlerAnnounced" />, so the registry fills without waiting for the next heartbeat.
/// </summary>
/// <param name="CorrelationId">Correlation id</param>
/// <param name="Timestamp">Time (UTC) of the request</param>
public record CkMethodHandlerDiscoveryRequested(Guid CorrelationId, DateTime Timestamp)
    : EventBase(CorrelationId, Timestamp)
{
    /// <summary>
    ///     Instance id of the requesting dispatcher.
    /// </summary>
    public required string RequestingInstanceId { get; init; }
}

/// <summary>
///     Well-known values of <see cref="CkMethodHandlerAnnounced.Tenants" />.
/// </summary>
public static class CkMethodHandlerTenants
{
    /// <summary>
    ///     The handler serves all tenants.
    /// </summary>
    public const string All = "*";
}
