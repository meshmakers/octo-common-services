namespace Meshmakers.Octo.Services.Methods;

/// <summary>Options of the method handler host (configuration section <c>Methods</c>).</summary>
public sealed class CkMethodHandlerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Methods";

    /// <summary>Smallest valid <see cref="HeartbeatInterval" /> (validated at startup).</summary>
    public static readonly TimeSpan MinHeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     <see cref="Ttl" /> must be at least this many times <see cref="HeartbeatInterval" /> (validated at startup), so a
    ///     live handler survives a lost heartbeat.
    /// </summary>
    public const int MinTtlToHeartbeatRatio = 2;

    /// <summary>Largest valid <see cref="Ttl" />: the clamp of the dispatcher registry (validated at startup).</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);

    /// <summary>
    ///     Interval of the announce heartbeat. Final default 15 s (AB#7838); at least 1 s, validated at startup
    ///     (<c>Methods:HeartbeatInterval</c>, environment variable <c>OCTO_Methods__HeartbeatInterval</c>).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Time to live of an announcement without a heartbeat. Final default 45 s (AB#7838); at least twice
    ///     <see cref="HeartbeatInterval" /> and at most 24 h, validated at startup (<c>Methods:Ttl</c>, environment variable
    ///     <c>OCTO_Methods__Ttl</c>). Each announcement carries its own TTL, so hosts may use different values.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    ///     How long the host waits for every method endpoint to accept messages (queue and bindings exist on the
    ///     broker) before it announces the handler. When the time runs out the service fails to start instead of
    ///     announcing a handler that cannot receive requests. Default 30 s.
    /// </summary>
    public TimeSpan EndpointReadyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Validate every registration against the CK cache at startup (unknown method id, parameter and result shape) and
    ///     fail fast. Switch off only in tests and in services that run without a CK cache.
    /// </summary>
    public bool ValidateRegistrations { get; set; } = true;

    /// <summary>
    ///     Tenants whose CK cache is searched for the method definitions. A method must be found in at least one of
    ///     them. Empty means the system tenant (<c>OctoSystemConfiguration.SystemTenantId</c>), which holds every
    ///     System model.
    /// </summary>
    public List<string> ValidationTenantIds { get; set; } = [];
}
