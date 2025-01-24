using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Notifications;

public class EventStoreFailedException : OctoServiceException
{
    public EventStoreFailedException()
    {
    }

    public EventStoreFailedException(string message) : base(message)
    {
    }

    public EventStoreFailedException(string message, Exception inner) : base(message, inner)
    {
    }
}

