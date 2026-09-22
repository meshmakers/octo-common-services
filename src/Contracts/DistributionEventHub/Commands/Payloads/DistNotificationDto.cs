namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

/// <summary>
/// Represents a notification.
/// </summary>
/// <param name="Subject">Subject of the notification.</param>
/// <param name="Body">Body of the notification.</param>
/// <param name="Recipient">
/// E-mail address of the recipient. Optional since AB#5217: a notification addressed by
/// <see cref="RecipientSubjectId" /> leaves this null and lets the delivery pipeline resolve the
/// address from the directory.
/// </param>
/// <param name="Cc">Carbon copy recipient of the notification.</param>
/// <param name="Bcc">Blind carbon copy recipient of the notification.</param>
/// <remarks>
/// <para>
/// The members added by AB#5217 are <b>init-only properties, not positional parameters</b>. The
/// positional list is a wire contract with live producers and consumers — appending to it changes
/// the constructor signature every caller compiles against, and changes the shape a consumer that
/// deserializes by constructor sees. Properties are additive on both sides: an old producer omits
/// them and they arrive null, a new producer sets them and an old consumer ignores them.
/// </para>
/// <para>
/// 🔴 Every member has to be writable by System.Text.Json, which is the serializer MassTransit uses
/// on the distribution event hub. This is the AB#5136 lesson: <c>SendNotificationsRequest</c>
/// carried a get-only collection next to a parameterized constructor, so the consumer deserialized
/// it as EMPTY and identity's OTP / welcome / reset mails were published but never delivered — with
/// no error anywhere, only a pipeline that "completed" in under a millisecond. Init accessors are
/// settable for System.Text.Json; <c>NotificationWireContractTests</c> proves it rather than
/// assuming it, and any member added here must be covered by that suite.
/// </para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public record DistNotificationDto(string Subject, string? Body, string? Recipient, string? Cc, string? Bcc)
{
    /// <summary>
    /// Runtime id of the recipient's <c>System.Identity/User</c> — the same value the identity
    /// service issues as the token <c>sub</c>.
    /// </summary>
    /// <remarks>
    /// This is what lets a system-initiated notification reach the user on their preferred channel:
    /// the delivery pipeline hands it to <c>ResolveNotificationChannel@1</c> (AB#5152), which
    /// resolves the verified bindings, the binding-specific preference and the deterministic
    /// effective channel. Without it a notification can only ever be an e-mail to a literal
    /// address, which is why AB#5152's channel resolution was reachable from application pipelines
    /// (they derive the subjectId themselves) but never from a service.
    ///
    /// Deliberately singular. A rule matching a group produces one notification PER recipient, not
    /// one notification with many: delivery status, channel resolution and the resend of a single
    /// failed delivery are all per recipient (AB#5223).
    /// </remarks>
    public string? RecipientSubjectId { get; init; }

    /// <summary>
    /// Forces delivery on one channel regardless of the recipient's preference — <c>"EMAIL"</c>,
    /// <c>"SIGNAL"</c> or <c>"TEAMS"</c>. Null means "honour the preference", which is the normal
    /// case.
    /// </summary>
    /// <remarks>
    /// Exists for security messages: an OTP must arrive on the channel the enrolment is being
    /// proven for, never on whichever channel the user happens to prefer. A string rather than an
    /// enum, matching the channel vocabulary already established by
    /// <c>VerifiedPrincipal.PreferredChannel</c> and <c>ResolveNotificationChannel@1</c>'s
    /// <c>effectiveChannel</c> — keeping octo-communication-sdk out of this change.
    /// </remarks>
    public string? ForcedChannel { get; init; }

    /// <summary>
    /// Well-known name of the <c>System.Notification/NotificationTemplate</c> this notification was
    /// rendered from.
    /// </summary>
    /// <remarks>
    /// Carried for the delivery record (AB#5223) and for per-channel rendering (AB#5222): the
    /// subject and body here are already rendered for e-mail, and a pipeline that wants the SMS or
    /// Signal variant of the same template needs to know which template it was.
    /// </remarks>
    public string? TemplateName { get; init; }

    /// <summary>
    /// Runtime id of what caused this notification — the <c>System.Notification/Event</c> behind an
    /// alarm, or the rule that matched.
    /// </summary>
    /// <remarks>
    /// The join key between the event log, the rule that fired and the delivery record, so
    /// "did the alarm mail actually go out" is answerable without correlating on timestamps.
    /// A string rather than an <c>OctoObjectId</c> so the contracts assembly keeps no dependency on
    /// the runtime types; the value is the ordinary 24-character hex rtId.
    /// </remarks>
    public string? CorrelationRtId { get; init; }
}
