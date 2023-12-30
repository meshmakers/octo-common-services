using System.Net;

namespace Meshmakers.Octo.Services.Common.ApiErrors;

/// <summary>
///     Represents an internal server error data transfer object
/// </summary>
public class OperationFailedError : ApiError
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public OperationFailedError()
        : base(HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError.ToString())
    {
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="message">A message that describes the error</param>
    public OperationFailedError(string message)
        : base(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest.ToString(), message)
    {
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="message">A message that describes the error</param>
    /// <param name="failedDetails">A list of that describes the error</param>
    public OperationFailedError(string message, IEnumerable<FailedDetails> failedDetails)
        : base(HttpStatusCode.BadRequest, HttpStatusCode.BadRequest.ToString(), message, failedDetails)
    {
    }
}