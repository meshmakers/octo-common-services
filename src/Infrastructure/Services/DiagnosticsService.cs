using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NLog;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Implements a service for managing diagnostics.
/// </summary>
public class DiagnosticsService : IDiagnosticsService
{
    /// <summary>
    /// Reconfigure the log level of the service.
    /// </summary>
    /// <param name="minLogLevel">Minimal log level to be logged.</param>
    /// <returns></returns>
    public Task ReconfigureLogLevelAsync(LogLevelDto minLogLevel)
    {
        var logLevel = LogLevel.FromOrdinal((int)minLogLevel);
        foreach (var rule in LogManager.Configuration.LoggingRules)
        {
            rule.DisableLoggingForLevels(LogLevel.Trace, LogLevel.Fatal);
            rule.EnableLoggingForLevels(logLevel, LogLevel.Fatal);
        }

        LogManager.ReconfigExistingLoggers();

        return Task.CompletedTask;
    }
}