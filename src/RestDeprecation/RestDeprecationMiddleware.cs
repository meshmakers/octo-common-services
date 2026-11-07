using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Services.RestDeprecation;

/// <summary>
///     Adds the deprecation headers to every response of an endpoint marked with
///     <see cref="OctoRestDeprecatedAttribute" /> and counts the call (AB#8421).
/// </summary>
/// <remarks>
///     <para>
///         The middleware runs first in the pipeline (installed by <see cref="RestDeprecationStartupFilter" />), before
///         routing has selected an endpoint, and only registers a <see cref="HttpResponse.OnStarting(Func{object, Task}, object)" />
///         callback. The callback runs when the response starts — after routing, authorization, model validation and the
///         action — so it sees the selected endpoint and the final status code, and it covers responses that never reach
///         MVC: a 401/403 of the authorization middleware or a 400 of model validation.
///     </para>
///     <para>
///         The exception handler middleware clears the endpoint before it writes a 500; the endpoint of the failed request
///         is then taken from <see cref="IExceptionHandlerFeature.Endpoint" />, so 5xx answers carry the headers too.
///     </para>
///     <para>
///         An unmarked endpoint gets no header and no measurement; the callback returns after one metadata lookup.
///     </para>
/// </remarks>
internal sealed class RestDeprecationMiddleware(RequestDelegate next)
{
    private static readonly Func<object, Task> OnStartingCallback = OnStarting;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(OnStartingCallback, context);
        return next(context);
    }

    private static Task OnStarting(object state)
    {
        var context = (HttpContext)state;
        var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        var deprecation = endpoint?.Metadata.GetMetadata<OctoRestDeprecatedAttribute>();
        if (endpoint == null || deprecation == null)
        {
            return Task.CompletedTask;
        }

        var headers = context.Response.Headers;
        headers["Deprecation"] = deprecation.DeprecationHeader;
        headers["Sunset"] = deprecation.SunsetHeader;
        // Link may already carry other relations (paging, …); RFC 8288 allows one header with several values.
        headers.Append("Link", deprecation.LinkHeader);
        if (deprecation.SuccessorLinkHeader != null)
        {
            headers.Append("Link", deprecation.SuccessorLinkHeader);
        }

        RestDeprecationMetrics.RecordCall(context, endpoint);
        return Task.CompletedTask;
    }
}
