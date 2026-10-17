using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Startup validation of the in-process registrations (Order 15, same as the remote validation): a method must not be
///     registered both in-process and as a remote handler of the same service, and every registration must match its CK
///     definition (unknown method id, parameter and result shape). A failure stops the service start.
/// </summary>
internal sealed class CkInProcessMethodValidationService(
    CkInProcessMethodRegistrations registrations,
    IServiceProvider services,
    CkMethodRegistrationValidator validator,
    IOptions<CkMethodHandlerOptions> options,
    ILogger<CkInProcessMethodValidationService> logger) : IAsyncInitializationService
{
    public int Order => 15;

    public Task InitializeAsync()
    {
        var remote = (CkMethodHandlerRegistrations?)services.GetService(typeof(CkMethodHandlerRegistrations));
        var both = remote == null
            ? []
            : registrations.Handlers.Keys.Where(remote.Handlers.ContainsKey).Order(StringComparer.Ordinal).ToList();
        if (both.Count > 0)
        {
            throw new InvalidOperationException(
                $"CK method(s) {string.Join(", ", both.Select(m => $"'{m}'"))} are registered in-process and as remote " +
                "handler. Register a method in exactly one place: the dispatcher would never reach the remote handler.");
        }

        if (!options.Value.ValidateRegistrations)
        {
            logger.LogWarning(
                "In-process CK method registrations are NOT validated (Methods:ValidateRegistrations=false)");
            return Task.CompletedTask;
        }

        validator.Validate(registrations);
        logger.LogInformation("{Count} in-process CK method registration(s) validated against the CK cache",
            registrations.Handlers.Count);
        return Task.CompletedTask;
    }
}
