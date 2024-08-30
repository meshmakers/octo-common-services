using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Implements a service for managing diagnostics.
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>
    /// Reconfigure the log level of the service.
    /// </summary>
    /// <param name="minLogLevel">Minimal log level to be logged.</param>
    /// <returns></returns>
    Task ReconfigureLogLevelAsync(LogLevelDto minLogLevel);
}