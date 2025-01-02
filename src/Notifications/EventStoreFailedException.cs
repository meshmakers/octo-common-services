namespace Meshmakers.Octo.Services.Notifications;

public class EventStoreFailedException : Exception
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

