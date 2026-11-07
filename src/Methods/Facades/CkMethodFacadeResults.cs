using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Services.Methods.Facades;

/// <summary>
///     Maps the <see cref="CkMethodResponse" /> envelope of a CK method call to an ASP.NET Core result, for a REST endpoint
///     that serves a method (a "REST facade", AB#8411). The platform codes get documented default status codes
///     (<see cref="DefaultStatusCodes" />); a code the method declares gets <see cref="DeclaredErrorStatusCode" />. An
///     endpoint keeps its existing status codes and bodies with <see cref="CkMethodFacadeOptions" />.
/// </summary>
/// <example>
///     <code>
///     private static readonly CkMethodFacadeOptions Facade = new CkMethodFacadeOptions()
///         .MapError("PASSWORD_POLICY_VIOLATED", error => new BadRequestObjectResult(error.Details))
///         .MapSuccess(_ => new NoContentResult());
///
///     using (CkMethodPrincipalScope.Begin(CkMethodPrincipal.FromClaimsPrincipal(User)))
///     {
///         var response = await dispatcher.InvokeAsync(request, HttpContext.RequestAborted);
///         return response.ToActionResult(Facade);
///     }
///     </code>
/// </example>
public static class CkMethodFacadeResults
{
    /// <summary>Status code of an error code the method declares (no <c>METHOD_</c> prefix) without an override.</summary>
    public const int DeclaredErrorStatusCode = StatusCodes.Status400BadRequest;

    /// <summary>Status code of a platform code that is not in <see cref="DefaultStatusCodes" /> (a newer code).</summary>
    public const int UnknownPlatformErrorStatusCode = StatusCodes.Status500InternalServerError;

    /// <summary>The documented default status code per platform code (<see cref="CkMethodErrorCodes" />).</summary>
    public static IReadOnlyDictionary<string, int> DefaultStatusCodes { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [CkMethodErrorCodes.MethodForbidden] = StatusCodes.Status403Forbidden,
        [CkMethodErrorCodes.MethodTargetNotFound] = StatusCodes.Status404NotFound,
        [CkMethodErrorCodes.MethodValidationFailed] = StatusCodes.Status400BadRequest,
        [CkMethodErrorCodes.MethodParametersTooLarge] = StatusCodes.Status413PayloadTooLarge,
        [CkMethodErrorCodes.MethodRequestInProgress] = StatusCodes.Status409Conflict,
        [CkMethodErrorCodes.MethodTimeout] = StatusCodes.Status504GatewayTimeout,
        [CkMethodErrorCodes.MethodHandlerNotAvailable] = StatusCodes.Status503ServiceUnavailable,
        [CkMethodErrorCodes.MethodContractVersionUnsupported] = StatusCodes.Status503ServiceUnavailable,
        [CkMethodErrorCodes.MethodNotSupportedByHandlerVersion] = StatusCodes.Status503ServiceUnavailable,
        [CkMethodErrorCodes.MethodHandlerFailed] = StatusCodes.Status500InternalServerError,
        [CkMethodErrorCodes.MethodOutcomeUnknown] = StatusCodes.Status500InternalServerError
    };

    /// <summary>
    ///     The default status code of an error code: <see cref="DefaultStatusCodes" /> for a platform code,
    ///     <see cref="UnknownPlatformErrorStatusCode" /> for an unknown platform code, <see cref="DeclaredErrorStatusCode" />
    ///     for a code the method declares.
    /// </summary>
    public static int StatusCodeFor(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (DefaultStatusCodes.TryGetValue(code, out var status))
        {
            return status;
        }

        return code.StartsWith(CkMethodErrorCodes.PlatformPrefix, StringComparison.Ordinal)
            ? UnknownPlatformErrorStatusCode
            : DeclaredErrorStatusCode;
    }

    /// <summary>
    ///     The result of a call. Success: the override of <see cref="CkMethodFacadeOptions.MapSuccess" />, else <c>202</c>
    ///     with the operation handle for an accepted (asynchronous) call, <c>200</c> with the result JSON
    ///     (<c>application/json</c>), or <c>204</c> for a method without a result. Failure: the
    ///     override of <see cref="CkMethodFacadeOptions.MapError(string, Func{CkMethodError, IActionResult})" /> for the
    ///     code, else <see cref="Problem" /> with <see cref="StatusCodeFor" />.
    /// </summary>
    /// <param name="response">The envelope the dispatcher returned.</param>
    /// <param name="options">The mapping of this endpoint; <c>null</c> for the defaults.</param>
    public static IActionResult ToActionResult(this CkMethodResponse response, CkMethodFacadeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Success)
        {
            if (options?.Success is { } success)
            {
                return success(response);
            }

            if (response.Operation is { } operation)
            {
                // accepted for asynchronous execution (F3.6): the caller polls the operation, there is no result yet
                return new ObjectResult(operation) { StatusCode = StatusCodes.Status202Accepted };
            }

            return string.IsNullOrEmpty(response.ResultJson)
                ? new NoContentResult()
                : new ContentResult
                {
                    StatusCode = StatusCodes.Status200OK,
                    ContentType = "application/json",
                    Content = response.ResultJson
                };
        }

        // a failed envelope always carries an error; a malformed one is a failure of the handler side
        var error = response.Error ?? CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed);
        return options != null && options.TryGetError(error.Code, out var mapped)
            ? mapped(error)
            : Problem(error, StatusCodeFor(error.Code));
    }

    /// <summary>
    ///     The default error body: RFC 9457 problem details (<c>application/problem+json</c>) with the code as
    ///     <c>title</c> and the extensions <c>code</c>, <c>parameters</c> and, when present, <c>details</c>. The error
    ///     parameters never carry parameter values of the call (wire contract), so the body is safe to return.
    /// </summary>
    /// <param name="error">The error of the envelope.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    public static ObjectResult Problem(CkMethodError error, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = new ProblemDetails { Status = statusCode, Title = error.Code };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["parameters"] = error.Parameters;
        if (error.Details is { Count: > 0 } details)
        {
            problem.Extensions["details"] = details;
        }

        var result = new ObjectResult(problem) { StatusCode = statusCode };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}

/// <summary>
///     The mapping of one REST facade endpoint (AB#8411): overrides per error code and for the success. Build it once per
///     endpoint (a static field); it is not changed while requests use it.
/// </summary>
public sealed class CkMethodFacadeOptions
{
    private readonly Dictionary<string, Func<CkMethodError, IActionResult>> _errors = new(StringComparer.Ordinal);

    /// <summary>The success mapping, <c>null</c> for the default.</summary>
    internal Func<CkMethodResponse, IActionResult>? Success { get; private set; }

    /// <summary>Answers <paramref name="code" /> (declared or platform) with <paramref name="statusCode" /> and the default body.</summary>
    public CkMethodFacadeOptions MapError(string code, int statusCode) =>
        MapError(code, error => CkMethodFacadeResults.Problem(error, statusCode));

    /// <summary>Answers <paramref name="code" /> (declared or platform) with the result <paramref name="map" /> builds.</summary>
    /// <exception cref="ArgumentException">The code is empty.</exception>
    public CkMethodFacadeOptions MapError(string code, Func<CkMethodError, IActionResult> map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(map);
        _errors[code] = map;
        return this;
    }

    /// <summary>Answers a successful call with the result <paramref name="map" /> builds (e.g. the existing body of the endpoint).</summary>
    public CkMethodFacadeOptions MapSuccess(Func<CkMethodResponse, IActionResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Success = map;
        return this;
    }

    internal bool TryGetError(string code, out Func<CkMethodError, IActionResult> map) =>
        _errors.TryGetValue(code, out map!);
}
