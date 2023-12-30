using System.Net;

namespace Meshmakers.Octo.Services.Common.ApiErrors;

/// <summary>
///     Represents an internal server error data transfer object
/// </summary>
public class InternalServerError : ApiError
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public InternalServerError()
        : base(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError.ToString())
    {
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="message">A message that describes the error</param>
    public InternalServerError(string message)
        : base(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError.ToString(), message)
    {
    }
}