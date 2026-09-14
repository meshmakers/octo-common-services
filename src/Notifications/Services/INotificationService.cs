namespace Meshmakers.Octo.Services.Notifications.Services;

/// <summary>
///     Interface to the notification service, that allows to send notifications to the user.
/// </summary>
public interface INotificationService
{
    /// <summary>
    ///     Sends a message to the recipient.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="bodyVariables">Optional body variables (the placeholders get replaced with the text returned by the delegate)</param>
    /// <param name="recipient">Optional recipient</param>
    /// <param name="cc">Optional carbon copy recipient</param>
    /// <param name="bcc">Optional blind carbon copy recipient</param>
    /// <param name="templateName">Name of the notification template</param>
    /// <param name="subjectVariables">Optional subject variables (the placeholders get replaced with the text returned by the delegate)</param>
    /// <returns></returns>
    Task SendComplexAsync(string tenantId, string templateName, string recipient,
        Dictionary<string, Func<string>>? subjectVariables = null,
        Dictionary<string, Func<string>>? bodyVariables = null, string? cc = null, string? bcc = null);

    /// <summary>
    ///     Sends a message to the recipient.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="variables">Optional subject and body variables (the placeholders get replaced with the text returned by the delegate)</param>
    /// <param name="recipient">Optional recipient</param>
    /// <param name="cc">Optional carbon copy recipient</param>
    /// <param name="bcc">Optional blind carbon copy recipient</param>
    /// <param name="templateName">Name of the notification template</param>
    /// <returns></returns>
    Task SendAsync(string tenantId, string templateName, string recipient,
        Dictionary<string, Func<string>>? variables = null, string? cc = null, string? bcc = null);

    /// <summary>
    ///     Sends a message to an OctoMesh USER, to be delivered on whichever channel that user
    ///     prefers (AB#5217).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The difference to <see cref="SendAsync" /> is the address: that one names a literal
    ///         e-mail address and can therefore only ever produce an e-mail. This one names the
    ///         recipient's identity and leaves the address unset, so the delivery pipeline resolves
    ///         it with <c>ResolveNotificationChannel@1</c> (AB#5152) — verified bindings, the
    ///         binding-specific preference, and e-mail as the universal fallback. Prefer it
    ///         whenever the recipient is a user rather than an address.
    ///     </para>
    ///     <para>
    ///         The subject and body are rendered here, as they always were. Channel resolution is
    ///         NOT done here: it needs the identity directory, and putting a directory read in
    ///         front of every service-side send would make a notification depend on identity being
    ///         reachable. The pipeline resolves at delivery time instead.
    ///     </para>
    /// </remarks>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="subjectId">
    ///     Runtime id of the recipient's <c>System.Identity/User</c> — the value the identity
    ///     service issues as the token <c>sub</c>.
    /// </param>
    /// <param name="templateName">Name of the notification template</param>
    /// <param name="variables">
    ///     Optional subject and body variables (the placeholders get replaced with the text
    ///     returned by the delegate)
    /// </param>
    /// <param name="forcedChannel">
    ///     Optional channel to deliver on regardless of the recipient's preference —
    ///     <c>"EMAIL"</c>, <c>"SIGNAL"</c> or <c>"TEAMS"</c>. Set it for security messages such as
    ///     an OTP, which must arrive on the channel being proven; leave it null otherwise.
    /// </param>
    /// <param name="correlationRtId">
    ///     Optional runtime id of what caused the notification — the event behind an alarm, or the
    ///     rule that matched. Joins the event log, the rule and the delivery record.
    /// </param>
    Task SendToUserAsync(string tenantId, string subjectId, string templateName,
        Dictionary<string, Func<string>>? variables = null, string? forcedChannel = null,
        string? correlationRtId = null);
}