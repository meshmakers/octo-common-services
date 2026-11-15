using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Validates the registrations against the CK cache before the bus starts (Order 15: after the default tenant
///     configuration has loaded the CK cache at Order 10, before the event hub at Order 20 and the announcement at 25).
///     A failure stops the service start.
/// </summary>
internal sealed class CkMethodHandlerValidationService(
    CkMethodHandlerRegistrations registrations,
    CkMethodRegistrationValidator validator,
    IOptions<CkMethodHandlerOptions> options,
    ILogger<CkMethodHandlerValidationService> logger) : IAsyncInitializationService
{
    public int Order => 15;

    public async Task InitializeAsync()
    {
        if (!options.Value.ValidateRegistrations)
        {
            logger.LogWarning("CK method handler registrations are NOT validated (Methods:ValidateRegistrations=false)");
            return;
        }

        // AB#8969: loads (or waits for) a validation tenant's CK cache instead of treating it as empty
        await validator.ValidateAsync(registrations, options.Value.CacheLoadTimeout, CancellationToken.None)
            .ConfigureAwait(false);
        logger.LogInformation("{Count} CK method handler registration(s) validated against the CK cache",
            registrations.Handlers.Count);
    }
}
