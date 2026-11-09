using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Operations;

/// <summary>
///     Settings of the operation runtime of asynchronous CK methods (configuration section <c>Methods:Operations</c>,
///     AB#8443, OD-5). Validated at the start of the service; an invalid value stops it with a message that names the
///     setting and its bounds, never the configured value.
/// </summary>
public sealed class CkMethodOperationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName + ":Operations";

    /// <summary>Default retention after completion: 7 days (OD-5).</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>Shortest retention: 1 hour.</summary>
    public static readonly TimeSpan MinRetention = TimeSpan.FromHours(1);

    /// <summary>Longest retention: 30 days.</summary>
    public static readonly TimeSpan MaxRetention = TimeSpan.FromDays(30);

    /// <summary>How long a final record stays readable after its completion (1 h to 30 d, default 7 d).</summary>
    public TimeSpan Retention { get; set; } = DefaultRetention;

    /// <summary>How often the handler host refreshes the heartbeat of its running operations (default 15 s, at least 1 s).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     After how long without a heartbeat an open operation counts as lost and the sweep ends it (default 2 min, at
    ///     least 3 x <see cref="HeartbeatInterval" />).
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the sweep looks for lost operations (default 1 min, at least 1 s).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Suggested delay before the caller's first status check, in seconds (envelope <c>retryAfterSeconds</c>, 1 to 3600, default 5).</summary>
    public int RetryAfterSeconds { get; set; } = 5;
}

/// <summary>Validates <see cref="CkMethodOperationOptions" />; the messages name the setting only.</summary>
internal sealed class CkMethodOperationOptionsValidator : IValidateOptions<CkMethodOperationOptions>
{
    public ValidateOptionsResult Validate(string? name, CkMethodOperationOptions options)
    {
        var failures = new List<string>();
        if (options.Retention < CkMethodOperationOptions.MinRetention ||
            options.Retention > CkMethodOperationOptions.MaxRetention)
        {
            failures.Add($"{Key(nameof(CkMethodOperationOptions.Retention))} must be between 1 hour and 30 days.");
        }

        if (options.HeartbeatInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{Key(nameof(CkMethodOperationOptions.HeartbeatInterval))} must be at least 1 second.");
        }

        if (options.StaleAfter < options.HeartbeatInterval * 3)
        {
            failures.Add(
                $"{Key(nameof(CkMethodOperationOptions.StaleAfter))} must be at least three times " +
                $"{Key(nameof(CkMethodOperationOptions.HeartbeatInterval))}.");
        }

        if (options.SweepInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{Key(nameof(CkMethodOperationOptions.SweepInterval))} must be at least 1 second.");
        }

        if (options.RetryAfterSeconds is < 1 or > 3600)
        {
            failures.Add($"{Key(nameof(CkMethodOperationOptions.RetryAfterSeconds))} must be between 1 and 3600.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Key(string setting) => CkMethodOperationOptions.SectionName + ":" + setting;
}
