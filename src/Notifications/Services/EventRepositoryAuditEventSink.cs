using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
/// <see cref="IAuditEventSink"/> implementation that persists every audit event to the
/// platform event log via <see cref="IEventRepository"/>. Replaces the per-interface bridges
/// (e.g. the old <c>EventRepositoryCkModelImportAuditTrail</c>) so a host's
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> only needs one
/// registration to surface every engine audit-trail call in the event log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="IServiceProvider"/> in the ctor instead of <c>IEventRepository</c>?</b>
/// <see cref="IEventRepository"/>'s implementation <c>EventRepository</c> ctor-injects
/// <see cref="Runtime.Contracts.MongoDb.ISystemContext"/>. The engine-side audit-trail
/// forwarders are resolved during <c>SystemContext</c> construction (they are pulled in via
/// <c>IDatabaseCkModelRepository</c>). A direct <c>IEventRepository</c> ctor dependency would
/// re-form the bootstrap cycle that this whole refactor is meant to break. Lazy-resolving
/// <see cref="IEventRepository"/> from a fresh scope inside <see cref="PublishAsync"/> keeps
/// the sink itself cycle-free, and audit publication happens long after the bootstrap is
/// done.
/// </para>
/// <para>
/// Failures while writing to the event repository are caught and logged. The audit-trail
/// contract says publishers must never fail because of bookkeeping issues — the engine
/// operation that triggered the event has to succeed.
/// </para>
/// </remarks>
public sealed class EventRepositoryAuditEventSink : IAuditEventSink
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RtEventSourcesEnum _defaultSource;
    private readonly ILogger<EventRepositoryAuditEventSink> _logger;

    public EventRepositoryAuditEventSink(
        IServiceProvider serviceProvider,
        IOptions<AuditEventSinkOptions> options,
        ILogger<EventRepositoryAuditEventSink> logger)
    {
        _serviceProvider = serviceProvider;
        _defaultSource = options.Value.DefaultSource;
        _logger = logger;
    }

    public async Task PublishAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            var level = MapLevel(auditEvent.Level);
            if (string.IsNullOrWhiteSpace(auditEvent.TenantId))
            {
                await eventRepository.StoreSystemEventAsync(_defaultSource, level, auditEvent.Message)
                    .ConfigureAwait(false);
            }
            else
            {
                await eventRepository.StoreEventAsync(auditEvent.TenantId, _defaultSource, level,
                    auditEvent.Message).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish audit event '{AuditCategory}' (tenant: {TenantId}) to event repository; " +
                "the engine operation that produced the event was not affected.",
                auditEvent.Category, auditEvent.TenantId ?? "<system>");
        }
    }

    private static RtEventLevelsEnum MapLevel(AuditEventLevel level) => level switch
    {
        AuditEventLevel.Information => RtEventLevelsEnum.Information,
        AuditEventLevel.Warning => RtEventLevelsEnum.Warning,
        AuditEventLevel.Error => RtEventLevelsEnum.Error,
        AuditEventLevel.Critical => RtEventLevelsEnum.Critical,
        _ => RtEventLevelsEnum.Information,
    };
}

/// <summary>
/// Options for <see cref="EventRepositoryAuditEventSink"/>. Hosts customise via
/// <c>services.Configure&lt;AuditEventSinkOptions&gt;(o =&gt; o.DefaultSource = …)</c> when
/// they want a different platform source than
/// <see cref="RtEventSourcesEnum.AssetRepositoryService"/> attached to every persisted event.
/// </summary>
public sealed class AuditEventSinkOptions
{
    /// <summary>
    /// <see cref="RtEventSourcesEnum"/> written into every persisted event. Hosts that want to
    /// differentiate per-category can wrap the sink and translate
    /// <see cref="AuditEvent.Category"/> to a finer-grained source.
    /// </summary>
    public RtEventSourcesEnum DefaultSource { get; set; } = RtEventSourcesEnum.AssetRepositoryService;
}
