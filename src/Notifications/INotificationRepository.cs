namespace Meshmakers.Octo.Services.Notifications;

/// <summary>
///     Interface for the notification repository.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    ///     Sends a message to the recipient.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="recipient">Optional recipient</param>
    /// <param name="cc">Optional carbon copy recipient</param>
    /// <param name="bcc">Optional blind carbon copy recipient</param>
    /// <param name="subject">The subject of the message</param>
    /// <param name="body">The body of the message</param>
    /// <returns></returns>
    Task SendMessageAsync(string tenantId, string subject, string? body, string? recipient, string? cc, string? bcc);
}