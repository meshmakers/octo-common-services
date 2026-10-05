using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using NLog;
using NLog.Config;
using NLog.Targets;

using Xunit;

namespace Observability.Tests;

/// <summary>
///     Pins the behaviour of the reference <c>nlog.config</c> that every OctoMesh service copies.
/// </summary>
/// <remarks>
///     <para>
///         AB#5478 §2.3. The file is loaded from disk — the real one, linked into this project's
///         output — rather than restated here, because a restated copy would drift and these tests
///         would keep passing while the shipped configuration degraded.
///     </para>
///     <para>
///         Every assertion below corresponds to one of the four defects the audit found in the old
///         pipe-delimited layout: no trace correlation, no severity text, parameters interpolated
///         into prose, and a multi-line stack trace inside the message. The last one is the subtle
///         one: the filelog receiver reads container log files line by line, so an unescaped stack
///         trace does not arrive as one record with awkward formatting — it arrives as dozens of
///         unrelated records, and the one carrying the exception type is not the one carrying the
///         message.
///     </para>
/// </remarks>
public class ReferenceLogConfigurationTests
{
    private const string ReferenceConfigFile = "nlog.reference.config";
    private const string TargetName = "console";
    private const string OctoLogLevelVariable = "OCTO_LOG_LEVEL";

    [Fact]
    public void TheReferenceConfigurationLoads_AndExposesTheConsoleTargetByName()
    {
        using var factory = LoadReferenceConfiguration();

        var configuration = factory.Configuration;
        Assert.NotNull(configuration);
        var target = configuration.FindTargetByName(TargetName);

        Assert.NotNull(target);
        Assert.IsType<ConsoleTarget>(target);
    }

    /// <summary>
    ///     A record emitted inside an activity must carry trace context, the severity as text, and
    ///     the message — each as its own JSON field.
    /// </summary>
    [Fact]
    public void ARecordInsideAnActivity_IsOneJsonObject_WithTraceContextAndSeverity()
    {
        using var factory = LoadReferenceConfiguration();
        using var activity = new Activity("octo.test").Start();

        var json = RenderAsJson(factory, LogEventInfo.Create(LogLevel.Warn, "Meshmakers.Octo.Test", "Batch completed"));

        Assert.Equal("WARN", (string?)json["level"]);
        Assert.Equal("Batch completed", (string?)json["message"]);
        Assert.Equal(activity.TraceId.ToHexString(), (string?)json["trace_id"]);
        Assert.Equal(activity.SpanId.ToHexString(), (string?)json["span_id"]);
        Assert.Equal("Meshmakers.Octo.Test", (string?)json["logger"]);
    }

    /// <summary>
    ///     The direct cause of AB#5425a: message parameters have to become fields, not prose.
    /// </summary>
    /// <remarks>
    ///     The live example from the audit was
    ///     <c>[energyiq] Batch completed 1/1 executions</c> — tenant and counts present, and
    ///     reachable only by substring match. With <c>includeEventProperties</c> the same call
    ///     yields queryable dimensions, which the collector then merges into the log record's
    ///     attributes.
    /// </remarks>
    [Fact]
    public void MessageParameters_BecomeTheirOwnFields()
    {
        using var factory = LoadReferenceConfiguration();

        var logEvent = new LogEventInfo(LogLevel.Info, "Meshmakers.Octo.Test",
            null, "Batch completed {Completed}/{Total} executions for tenant {TenantId}",
            [1, 1, "energyiq"]);

        var json = RenderAsJson(factory, logEvent);

        Assert.Equal(1, (int?)json["Completed"]);
        Assert.Equal(1, (int?)json["Total"]);
        Assert.Equal("energyiq", (string?)json["TenantId"]);
    }

    /// <summary>
    ///     Scope properties have to be rendered, which is what makes an
    ///     <c>ILogger.BeginScope</c> worth writing.
    /// </summary>
    /// <remarks>
    ///     §2.2 added the tenant as a span tag but deliberately skipped the logging scope, because
    ///     under the old layout a scope was provably inert — nothing rendered it. This assertion is
    ///     what changes that, so it is the precondition for putting the tenant on every log record
    ///     of the shared services.
    /// </remarks>
    [Fact]
    public void ScopeProperties_AreRendered()
    {
        using var factory = LoadReferenceConfiguration();
        var logger = factory.GetLogger("Meshmakers.Octo.Test");

        using (logger.PushScopeProperty("octo.tenant.id", "energyiq"))
        {
            var json = RenderAsJson(factory, LogEventInfo.Create(LogLevel.Info, "Meshmakers.Octo.Test", "hello"));

            Assert.Equal("energyiq", (string?)json["octo.tenant.id"]);
        }
    }

    /// <summary>
    ///     An exception must occupy its own fields and must not break the record into lines.
    /// </summary>
    [Fact]
    public void AnException_IsThreeFields_AndTheRecordStaysOnOneLine()
    {
        using var factory = LoadReferenceConfiguration();
        var exception = CaughtException();

        var rendered = Render(factory, LogEventInfo.Create(LogLevel.Error, "Meshmakers.Octo.Test", exception, null, "failed"));

        Assert.DoesNotContain('\n', rendered);
        Assert.DoesNotContain('\r', rendered);

        var json = JsonNode.Parse(rendered)!;
        Assert.Equal("System.InvalidOperationException", (string?)json["exception.type"]);
        Assert.Equal("deliberate", (string?)json["exception.message"]);
        Assert.Contains("deliberate", (string?)json["exception.stacktrace"]);
        Assert.Contains("CaughtException", (string?)json["exception.stacktrace"]);
    }

    /// <summary>
    ///     The level comes from the environment, not from the repository.
    /// </summary>
    /// <remarks>
    ///     This is the assertion worth having most: a layout in <c>minLevel</c> is an NLog 5+
    ///     feature, and if it were unsupported the attribute would not fail loudly — the rule
    ///     would simply never match the intended levels. The adapter's hard-coded
    ///     <c>minlevel="Debug"</c> is what this replaces.
    /// </remarks>
    [Theory]
    [InlineData("Debug", true)]
    [InlineData("Info", false)]
    [InlineData("Warn", false)]
    public void OctoLogLevel_FromTheEnvironment_ControlsWhetherDebugIsWritten(string level, bool debugEnabled)
    {
        var previous = Environment.GetEnvironmentVariable(OctoLogLevelVariable);
        try
        {
            Environment.SetEnvironmentVariable(OctoLogLevelVariable, level);
            using var factory = LoadReferenceConfiguration();

            var logger = factory.GetLogger("Meshmakers.Octo.Test");

            Assert.Equal(debugEnabled, logger.IsDebugEnabled);
            Assert.True(logger.IsErrorEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OctoLogLevelVariable, previous);
        }
    }

    /// <summary>
    ///     With no environment variable the default is Info — a service that ships without the
    ///     variable must not flood.
    /// </summary>
    [Fact]
    public void WithNoEnvironmentVariable_TheDefaultLevelIsInfo()
    {
        var previous = Environment.GetEnvironmentVariable(OctoLogLevelVariable);
        try
        {
            Environment.SetEnvironmentVariable(OctoLogLevelVariable, null);
            using var factory = LoadReferenceConfiguration();

            var logger = factory.GetLogger("Meshmakers.Octo.Test");

            Assert.False(logger.IsDebugEnabled);
            Assert.True(logger.IsInfoEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OctoLogLevelVariable, previous);
        }
    }

    private static LogFactory LoadReferenceConfiguration()
    {
        Assert.True(File.Exists(ReferenceConfigFile),
            $"{ReferenceConfigFile} is not next to the test assembly; the csproj link is broken.");

        var factory = new LogFactory();
        factory.Setup().LoadConfigurationFromFile(ReferenceConfigFile, optional: false);
        return factory;
    }

    private static string Render(LogFactory factory, LogEventInfo logEvent)
    {
        var configuration = factory.Configuration;
        Assert.NotNull(configuration);
        var target = Assert.IsType<ConsoleTarget>(configuration.FindTargetByName(TargetName));
        return target.Layout.Render(logEvent);
    }

    private static JsonNode RenderAsJson(LogFactory factory, LogEventInfo logEvent)
    {
        var rendered = Render(factory, logEvent);
        try
        {
            return JsonNode.Parse(rendered)!;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"The layout did not render valid JSON: {rendered}", exception);
        }
    }

    private static Exception CaughtException()
    {
        try
        {
            throw new InvalidOperationException("deliberate");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
