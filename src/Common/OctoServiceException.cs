namespace Meshmakers.Octo.Services.Common;

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
}

