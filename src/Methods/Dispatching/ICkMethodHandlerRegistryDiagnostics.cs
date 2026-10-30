namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Read-only diagnostics of the CK method handler registry of this service (AB#7836, F3.2): every handler row with its
///     source and state, the health of the persisted snapshot (AB#5671) and the methods served in-process. It returns
///     service data only: ids, service names, versions, times, served tenants and flags. A failure is reported as a flag
///     and an exception type name, never as a message (a message may carry connection data).
/// </summary>
public interface ICkMethodHandlerRegistryDiagnostics
{
    /// <summary>Reads the diagnostics at the current time of the dispatcher clock.</summary>
    CkMethodHandlerRegistryDiagnostics GetDiagnostics();
}

/// <summary>Diagnostics of the handler registry.</summary>
/// <param name="DispatcherInstanceId">Id of this dispatcher instance (as sent in the discovery request).</param>
/// <param name="GeneratedAtUtc">Time the state of every entry was evaluated at.</param>
/// <param name="Snapshot">State of the persisted snapshot.</param>
/// <param name="Entries">Every registry row (including expired and withdrawn ones, kept 24 h), ordered by method and instance.</param>
/// <param name="InProcessMethods">Methods served by the in-process transport of this service (they never announce); empty when none is registered.</param>
public sealed record CkMethodHandlerRegistryDiagnostics(
    string DispatcherInstanceId,
    DateTime GeneratedAtUtc,
    CkMethodHandlerSnapshotDiagnostics Snapshot,
    IReadOnlyList<CkMethodHandlerDiagnosticsEntry> Entries,
    IReadOnlyList<CkMethodInProcessDiagnosticsEntry> InProcessMethods);

/// <summary>State of the persisted registry snapshot.</summary>
/// <param name="Enabled">A snapshot store is present and <c>Methods:Dispatcher:SnapshotEnabled</c> is set.</param>
/// <param name="Load">The last load, or null when none ran (disabled, or not yet loaded).</param>
/// <param name="Flush">The last flushes.</param>
public sealed record CkMethodHandlerSnapshotDiagnostics(
    bool Enabled,
    CkMethodHandlerSnapshotLoadStatus? Load,
    CkMethodHandlerSnapshotFlushStatus Flush);

/// <summary>Result of a snapshot load.</summary>
/// <param name="LoadedAtUtc">Time of the load.</param>
/// <param name="RowsRead">Rows the store returned.</param>
/// <param name="RowsRestored">Rows taken over into the registry (older or newer-in-memory rows are skipped).</param>
/// <param name="Failed"><c>true</c> when the load failed; the registry then fills from heartbeats.</param>
/// <param name="FailureExceptionType">Type name of the exception when <paramref name="Failed" />; never a message.</param>
public sealed record CkMethodHandlerSnapshotLoadStatus(
    DateTime LoadedAtUtc,
    int RowsRead,
    int RowsRestored,
    bool Failed,
    string? FailureExceptionType);

/// <summary>State of the snapshot flushes.</summary>
/// <param name="LastSuccessUtc">Time of the last successful write, or null when none happened yet.</param>
/// <param name="LastFailed"><c>true</c> when the most recent write failed (cleared by the next successful write).</param>
/// <param name="LastFailureUtc">Time of the most recent failed write, or null.</param>
/// <param name="LastFailureExceptionType">Type name of the exception of the most recent failed write; never a message.</param>
public sealed record CkMethodHandlerSnapshotFlushStatus(
    DateTime? LastSuccessUtc,
    bool LastFailed,
    DateTime? LastFailureUtc,
    string? LastFailureExceptionType);

/// <summary>A registry row with its source and state.</summary>
/// <param name="MethodId">Qualified method id as announced.</param>
/// <param name="ServiceName">Handler service name.</param>
/// <param name="InstanceId">Handler instance id.</param>
/// <param name="Version">Informational handler service version.</param>
/// <param name="ContractVersion">Highest wire contract version the instance understands.</param>
/// <param name="LastSeenUtc">Last announcement or withdrawal received.</param>
/// <param name="ExpiresUtc">Live until then.</param>
/// <param name="Tenants">Served tenants (<c>"*"</c> = all).</param>
/// <param name="Source">Announcement, or Snapshot for a restored row not yet confirmed.</param>
/// <param name="State">Live, expired, withdrawn or no queue bound.</param>
/// <param name="Transport">The transport the row belongs to (<c>bus</c>).</param>
public sealed record CkMethodHandlerDiagnosticsEntry(
    string MethodId,
    string ServiceName,
    string InstanceId,
    string? Version,
    int ContractVersion,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc,
    IReadOnlyList<string> Tenants,
    CkMethodHandlerEntrySource Source,
    CkMethodHandlerState State,
    string Transport);

/// <summary>A method served in-process by this service.</summary>
/// <param name="MethodId">Qualified method id.</param>
/// <param name="Transport">Always <c>in-process</c> (<see cref="CkMethodTransportNames.InProcess" />).</param>
public sealed record CkMethodInProcessDiagnosticsEntry(string MethodId, string Transport);
