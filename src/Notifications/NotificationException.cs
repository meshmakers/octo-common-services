using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Notifications;

public class NotificationException : OctoServiceException
{
    private NotificationException()
    {
    }

    private NotificationException(string message) : base(message)
    {
    }

    private NotificationException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception TemplateAmbiguous(string templateName)
    {
        return new NotificationException($"Template '{templateName}' is ambiguous.");
    }

    public static Exception TemplateNotFound(string templateName)
    {
        return new NotificationException($"Template '{templateName}' not found.");
    }

    public static Exception TemplateInvalid(string templateName)
    {
        return new NotificationException($"Template '{templateName}' is invalid.");
    }
}
