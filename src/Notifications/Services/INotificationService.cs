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
}