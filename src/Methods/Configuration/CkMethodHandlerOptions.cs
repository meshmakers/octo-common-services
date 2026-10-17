namespace Meshmakers.Octo.Services.Methods;

/// <summary>Options of the method handler host (configuration section <c>Methods</c>).</summary>
public sealed class CkMethodHandlerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Methods";

    /// <summary>Interval of the announce heartbeat. The contract default is 15 s.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Time to live of an announcement without a heartbeat. The contract default is 45 s.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);

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
