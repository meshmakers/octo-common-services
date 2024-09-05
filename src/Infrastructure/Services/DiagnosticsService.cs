using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NLog;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
/// Implements a service for managing diagnostics.
/// </summary>
public class DiagnosticsService : IDiagnosticsService
{
    public Task ReconfigureLogLevelAsync(LogLevelDto minLogLevel, LogLevelDto maxLogLevel = LogLevelDto.Fatal,
        string loggerName = "Meshmakers.*")
    {
        var minLevel = LogLevel.FromOrdinal((int)minLogLevel);
        var maxLevel = LogLevel.FromOrdinal((int)maxLogLevel);
        foreach (var rule in LogManager.Configuration.LoggingRules)
        {
            if (rule.LoggerNamePattern == loggerName)
            {
                rule.DisableLoggingForLevels(LogLevel.Trace, LogLevel.Fatal);
                rule.EnableLoggingForLevels(minLevel, maxLevel);
            }
        }
        
        LogManager.ReconfigExistingLoggers();

        return Task.CompletedTask;
    }
}