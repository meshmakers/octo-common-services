using System.Net;
using Newtonsoft.Json;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Services.Contracts.ApiErrors;

/// <summary>
///     Represents an api error
/// </summary>
public abstract class ApiError
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="statusCode">Http status code</param>
    /// <param name="statusDescription">String representation of status code</param>
    protected ApiError(HttpStatusCode statusCode, string statusDescription)
    {
        StatusCode = (int)statusCode;
        StatusDescription = statusDescription;
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="statusCode">Http status code</param>
    /// <param name="statusDescription">String representation of status code</param>
    /// <param name="message">A message that explains what actually happened</param>
    protected ApiError(HttpStatusCode statusCode, string statusDescription, string message)
        : this(statusCode, statusDescription)
    {
        Message = message;
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="statusCode">Http status code</param>
    /// <param name="statusDescription">String representation of status code</param>
    /// <param name="message">A message that explains what actually happened</param>
    /// <param name="details">Details to the error</param>
    protected ApiError(HttpStatusCode statusCode, string statusDescription, string message,
        IEnumerable<FailedDetails> details)
        : this(statusCode, statusDescription, message)
    {
        Details = details;
    }

    /// <summary>
    ///     The Http status code
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    ///     String representation of the status code
    /// </summary>
    public string StatusDescription { get; }

    /// <summary>
    ///     A message that explains what actually happened
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public string? Message { get; private set; }

    /// <summary>
    ///     A message that explains what actually happened
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public IEnumerable<FailedDetails>? Details { get; private set; }
}