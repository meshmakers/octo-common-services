using Meshmakers.Octo.Runtime.Contracts.AuditTrails;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     An <see cref="IAuditEventSink" /> that can also report a failed write (AB#6919). <see cref="IAuditEventSink.PublishAsync" />
///     never fails by contract, which is right for bookkeeping of engine operations but hides the failure from a caller that must
///     know whether the record exists (the CK method audit of a regulated tenant). <see cref="PublishStrictAsync" /> writes the
///     same event and lets the exception of the store escape.
/// </summary>
public interface IStrictAuditEventSink : IAuditEventSink
{
    /// <summary>Writes the event; throws when it could not be stored.</summary>
    /// <param name="auditEvent">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task PublishStrictAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
