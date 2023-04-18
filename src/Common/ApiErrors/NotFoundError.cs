using System.Net;

namespace Meshmakers.Octo.Backend.Common.ApiErrors;

/// <summary>
///     Represents a not found error data transfer object
/// </summary>
public class NotFoundError : ApiError
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public NotFoundError()
        : base(HttpStatusCode.NotFound, HttpStatusCode.NotFound.ToString())
    {
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="message">A message that describes the error</param>
    public NotFoundError(string message)
        : base(HttpStatusCode.NotFound, HttpStatusCode.NotFound.ToString(), message)
    {
    }
}
