using System.Collections;
using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.AuditTrails;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
/// <see cref="IAuditEventSink"/> implementation that persists every audit event to the
/// platform event log via <see cref="IEventRepository"/>, including the audit event's category and its
/// metadata as structured <c>Details</c> (System.Notification 2.7.0, AB#7821). Replaces the per-interface bridges
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
public sealed class EventRepositoryAuditEventSink : IStrictAuditEventSink
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
            await PublishStrictAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish audit event '{AuditCategory}' (tenant: {TenantId}) to event repository; " +
                "the engine operation that produced the event was not affected.",
                auditEvent.Category, auditEvent.TenantId ?? "<system>");
        }
    }

    /// <summary>
    /// Writes the event like <see cref="PublishAsync"/> but lets the failure of the event repository escape (AB#6919),
    /// for the caller that must know whether the record exists.
    /// </summary>
    public async Task PublishStrictAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var level = MapLevel(auditEvent.Level);
        var details = MapMetadata(auditEvent.Metadata);
        if (string.IsNullOrWhiteSpace(auditEvent.TenantId))
        {
            await eventRepository.StoreSystemEventAsync(_defaultSource, level, auditEvent.Message,
                    auditEvent.Category, details)
                .ConfigureAwait(false);
        }
        else
        {
            await eventRepository.StoreEventAsync(auditEvent.TenantId, _defaultSource, level,
                auditEvent.Message, auditEvent.Category, details).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps <see cref="AuditEvent.Metadata"/> to the event's key/value details (AB#7821): entries whose
    /// value is <c>null</c> are skipped and values are rendered with the invariant culture (dates as
    /// ISO 8601 round-trip, lists comma-separated), so the stored text does not depend on the server's locale.
    /// </summary>
    internal static IReadOnlyCollection<KeyValuePair<string, string>>? MapMetadata(
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var details = new List<KeyValuePair<string, string>>(metadata.Count);
        foreach (var (key, value) in metadata)
        {
            if (value is null || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var text = Render(value);
            details.Add(new KeyValuePair<string, string>(key, text));
        }

        return details.Count == 0 ? null : details;
    }

    private static string Render(object value) => value switch
    {
        string s => s,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        // Lists (for example the missing attribute ids of an import) become one comma-separated text, not the type name.
        IEnumerable items => string.Join(", ", items.Cast<object?>().Where(i => i is not null).Select(i => Render(i!))),
        _ => value.ToString() ?? string.Empty,
    };

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
