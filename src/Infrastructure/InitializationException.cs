namespace Meshmakers.Octo.Services.Infrastructure;

public class InitializationException : OctoServiceException
{
    public InitializationException()
    {
    }

    public InitializationException(string message) : base(message)
    {
    }

    public InitializationException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception ImportCkModelFailed(string tenantId, string messages)
    {
        return new InitializationException($"Importing CK model failed for system context tenant '{tenantId}'. {messages}");
    }
}