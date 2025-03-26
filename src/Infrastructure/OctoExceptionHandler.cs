using Meshmakers.Common.Shared;
using Microsoft.AspNetCore.Diagnostics;

namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
/// Exception handler for octo services to log exceptions
/// </summary>
/// <param name="logger"></param>
internal class OctoExceptionHandler(ILogger<OctoExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError("Error Message: {ExceptionMessage}", exception.GetDirectAndIndirectMessages());
        return ValueTask.FromResult(false);
    }
}