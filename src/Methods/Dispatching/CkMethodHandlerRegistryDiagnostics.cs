namespace Meshmakers.Octo.Services.Methods.Dispatching;

/// <summary>Default <see cref="ICkMethodHandlerRegistryDiagnostics" />: reads the registry and the snapshot persistence.</summary>
internal sealed class CkMethodHandlerRegistryDiagnosticsService(
    CkMethodHandlerRegistry registry,
    CkMethodHandlerSnapshotPersistence persistence,
    TimeProvider timeProvider,
    ICkInProcessMethodTransport? inProcessTransport = null) : ICkMethodHandlerRegistryDiagnostics
{
    public CkMethodHandlerRegistryDiagnostics GetDiagnostics()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entries = registry.Snapshot()
            .OrderBy(e => e.MethodId, StringComparer.Ordinal)
            .ThenBy(e => e.InstanceId, StringComparer.Ordinal)
            .Select(e => new CkMethodHandlerDiagnosticsEntry(e.MethodId, e.ServiceName, e.InstanceId, e.Version,
                e.ContractVersion, e.LastSeenUtc, e.ExpiresUtc, e.Tenants.ToList(), e.Source, e.GetState(now), CkMethodTransportNames.Bus))
            .ToList();
        var inProcess = (inProcessTransport?.MethodIds ?? [])
            .OrderBy(m => m, StringComparer.Ordinal)
            .Select(m => new CkMethodInProcessDiagnosticsEntry(m, CkMethodTransportNames.InProcess))
            .ToList();
        var snapshot = new CkMethodHandlerSnapshotDiagnostics(persistence.IsEnabled, persistence.LoadStatus,
            persistence.FlushStatus);
        return new CkMethodHandlerRegistryDiagnostics(CkMethodDispatcherStartupService.DispatcherInstanceId, now, snapshot,
            entries, inProcess);
    }
}
