namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Tunables of the in-memory tenant setup retry (AB#7249). Bound from the configuration section
///     <see cref="SectionName" /> (<c>OCTO_TENANTSETUPRETRY__*</c>) by <c>AddOctoServiceInfrastructure</c>.
/// </summary>
public class TenantSetupRetryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "TenantSetupRetry";

    /// <summary>
    ///     Delay before the first retry of a failed tenant setup. Every further failure doubles it up to
    ///     <see cref="MaxDelay" />. The retry timer ticks every 30 s, so smaller values behave like 30 s.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the exponential backoff. There is no attempt limit: the retry never gives up.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     A tenant whose setup (or deferred start) has been failing for longer than this makes the
    ///     <c>TenantSetup</c> health check report <c>Degraded</c>. Default 10 minutes.
    /// </summary>
    public TimeSpan DegradedAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay after <paramref name="failedAttempts" /> failures (1 = after the first failure).</summary>
    public TimeSpan DelayAfter(int failedAttempts)
    {
        var initial = InitialDelay > TimeSpan.Zero ? InitialDelay : TimeSpan.FromSeconds(30);
        var max = MaxDelay >= initial ? MaxDelay : initial;
        var exponent = Math.Clamp(failedAttempts - 1, 0, 30);
        var ticks = initial.Ticks * Math.Pow(2, exponent);
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }
}
