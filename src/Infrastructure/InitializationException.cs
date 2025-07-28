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

    public static Exception EnsureLicenseKey(string service, string licenseKey)
    {
        return new InitializationException(
            $"The license key for {service} is not set. Please set the environment variable '{licenseKey}' to a valid license key.");
    }
}