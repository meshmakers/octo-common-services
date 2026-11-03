using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Infrastructure.Middleware;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
/// Exception handler for octo services to log exceptions
/// </summary>
/// <param name="logger"></param>
internal class OctoExceptionHandler(ILogger<OctoExceptionHandler> logger, TenantDatabaseUserHealer? healer = null)
    : IExceptionHandler
{
    private readonly TenantDatabaseUserHealer _healer = healer ?? TenantDatabaseUserHealer.Default;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError("Error Message: {ExceptionMessage}", exception.GetDirectAndIndirectMessages());

        // AB#8228: UseExceptionHandler swallows the exception, so it never reaches the eviction middleware.
        await TenantPoolEviction.EvictAsync(httpContext, exception, _healer).ConfigureAwait(false);
        return false;
    }
}