using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
/// Bridges <see cref="ICkModelImportAuditTrail"/> events to the platform event repository so
/// import notifications appear in the tenant's (or system tenant's) event log. Mirrors the
/// pattern documented for <c>IArchiveAuditTrail</c>: a host registers this adapter to replace
/// the default logging implementation.
/// </summary>
/// <remarks>
/// Failures while writing to the event repository are caught and logged. The CK model import
/// itself must not fail because audit-trail bookkeeping failed.
/// </remarks>
public sealed class EventRepositoryCkModelImportAuditTrail : ICkModelImportAuditTrail
{
    private readonly IEventRepository _eventRepository;
    private readonly RtEventSourcesEnum _source;
    private readonly ILogger<EventRepositoryCkModelImportAuditTrail> _logger;

    /// <summary>
    /// Creates a new adapter.
    /// </summary>
    /// <param name="eventRepository">The platform event repository.</param>
    /// <param name="logger">Logger for adapter-internal failures.</param>
    /// <param name="source">The event source used when storing events. Defaults to
    /// <see cref="RtEventSourcesEnum.AssetRepositoryService"/> because CK model imports are
    /// initiated from the asset repository service in practice; pass a different value when
    /// registering the adapter in a service with a different source identity.</param>
    public EventRepositoryCkModelImportAuditTrail(
        IEventRepository eventRepository,
        ILogger<EventRepositoryCkModelImportAuditTrail> logger,
        RtEventSourcesEnum source = RtEventSourcesEnum.AssetRepositoryService)
    {
        _eventRepository = eventRepository;
        _source = source;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordExtensibleEnumValueOverrideAsync(
        string? tenantId,
        CkModelId ckModelId,
        CkId<CkEnumId> ckEnumId,
        string ckEnumValueName,
        string extensionValueName,
        int extensionValueKey)
    {
        var message =
            $"Extension enum value '{extensionValueName}' (key: {extensionValueKey}) overrides " +
            $"CK-defined value '{ckEnumValueName}' for enum '{ckEnumId}' during import of CK model " +
            $"'{ckModelId}'. The custom extension value takes precedence over the construction kit " +
            "definition.";

        try
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                await _eventRepository.StoreSystemWarningEvent(_source, message);
            }
            else
            {
                await _eventRepository.StoreWarningEvent(tenantId, _source, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to record CK model import warning in event log for enum '{CkEnumId}' " +
                "(tenant: {TenantId}); the import itself completed.",
                ckEnumId, tenantId ?? "<system>");
        }
    }
}
