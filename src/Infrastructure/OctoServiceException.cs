namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
/// Represents an exception in an octo service.
/// </summary>
public class OctoServiceException : Exception
{
    /// <inheritdoc />
    public OctoServiceException()
    {
    }

    /// <inheritdoc />
    public OctoServiceException(string message) : base(message)
    {
    }

    /// <inheritdoc />
    public OctoServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception HttpContextNotCreated()
    {
        return new OctoServiceException(
            "The HTTP context is not created. This usually happens when the service scope is not created or the HTTP context is not available.");
    }

    public static Exception TenantIdNotFound()
    {
        return new OctoServiceException(
            "The tenant ID is not found in the HTTP context. This usually happens when the tenant ID is not set in the request or the HTTP context is not available.");
    }
}