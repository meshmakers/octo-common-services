using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NLog;
using NLog.Config;
using NLog.Targets;
using LogLevel = NLog.LogLevel;

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

        if (LogManager.Configuration == null)
        {
            throw ConfigurationException.LogManagerConfigurationNotFound();
        }

        var loggingRule = LogManager.Configuration.LoggingRules.SingleOrDefault(r => r.LoggerNamePattern == loggerName);
        
        // If the logger is disabled, remove the logging rule
        if (minLevel == LogLevel.Off && maxLevel == LogLevel.Off && loggerName != "*")
        {
            if (loggingRule != null)
            {
                LogManager.Configuration.LoggingRules.Remove(loggingRule);
                LogManager.ReconfigExistingLoggers();
            }
            return Task.CompletedTask;
        }
        
        // Create a new logging rule if it does not exist
        if (loggingRule == null)
        {
            var target = FindConsoleTarget();
            loggingRule = new LoggingRule(loggerName, target);
            LogManager.Configuration.LoggingRules.Insert(0, loggingRule);
        }
        
        // Configure the logging rule
        loggingRule.DisableLoggingForLevels(LogLevel.Trace, LogLevel.Fatal);
        loggingRule.EnableLoggingForLevels(minLevel, maxLevel);

        LogManager.ReconfigExistingLoggers();

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Resolves the console target of the service's <c>nlog.config</c> under either of the two
    ///     names in use.
    /// </summary>
    /// <remarks>
    ///     AB#5478 §2.3 renamed the target from <c>coloredConsole</c> to <c>console</c>, because the
    ///     structured JSON layout that replaced the pipe-delimited one has no colour to speak of —
    ///     nothing reads the escape codes, the only consumer being a container log file.
    ///     <para>
    ///         Both names are accepted on purpose. Each service carries its own copy of
    ///         <c>nlog.config</c> in its own repository with its own release train, so the fleet is
    ///         mixed for as long as the rollout takes. Resolving only the new name would mean this
    ///         method — the one that lets an operator raise a running service's log level without a
    ///         redeploy — throwing on every service that had not migrated yet.
    ///     </para>
    /// </remarks>
    private static Target FindConsoleTarget()
    {
        return LogManager.Configuration!.FindTargetByName("console")
               ?? LogManager.Configuration.FindTargetByName("coloredConsole")
               ?? throw ConfigurationException.TargetNotConfigured("console/coloredConsole");
    }
}