using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     Lifetime of an idempotency record (configuration key <c>Methods:Idempotency:Ttl</c>, OD-4 row 5). Default 24 hours,
///     maximum 7 days; a larger value is clamped to the maximum and logged once, zero or negative fails the start of the
///     service. The fingerprint key options are not part of this class (AB#7819).
/// </summary>
public sealed class CkMethodIdempotencyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName + ":Idempotency";

    /// <summary>The default lifetime, 24 hours.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>The largest lifetime, 7 days.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromDays(7);

    /// <summary>Lifetime of a record, counted from the first claim; <c>null</c> = <see cref="DefaultTtl" />.</summary>
    public TimeSpan? Ttl { get; set; }
}

/// <summary>Fails the start of the service when <see cref="CkMethodIdempotencyOptions.Ttl" /> is zero or negative.</summary>
internal sealed class CkMethodIdempotencyOptionsValidator : IValidateOptions<CkMethodIdempotencyOptions>
{
    public ValidateOptionsResult Validate(string? name, CkMethodIdempotencyOptions options) =>
        options.Ttl is { } ttl && ttl <= TimeSpan.Zero
            ? ValidateOptionsResult.Fail(
                $"{CkMethodIdempotencyOptions.SectionName}:{nameof(CkMethodIdempotencyOptions.Ttl)} must be greater than zero " +
                $"(default {CkMethodIdempotencyOptions.DefaultTtl}, maximum {CkMethodIdempotencyOptions.MaxTtl}).")
            : ValidateOptionsResult.Success;
}

/// <summary>
///     The effective lifetime of a record, resolved once from the options: default when unset, clamped to the maximum with
///     one warning at construction. Every store takes its <c>ExpiresAt</c> from here.
/// </summary>
public sealed class CkMethodIdempotencyLifetime
{
    /// <summary>Resolves the lifetime from <paramref name="options" />.</summary>
    public CkMethodIdempotencyLifetime(IOptions<CkMethodIdempotencyOptions> options,
        ILogger<CkMethodIdempotencyLifetime> logger)
    {
        var configured = options.Value.Ttl;
        if (configured is null)
        {
            Ttl = CkMethodIdempotencyOptions.DefaultTtl;
        }
        else if (configured.Value > CkMethodIdempotencyOptions.MaxTtl)
        {
            Ttl = CkMethodIdempotencyOptions.MaxTtl;
            logger.LogWarning(
                "{Section}:{Setting} {Configured} is above the maximum, clamped to {Maximum}",
                CkMethodIdempotencyOptions.SectionName, nameof(CkMethodIdempotencyOptions.Ttl), configured.Value,
                CkMethodIdempotencyOptions.MaxTtl);
        }
        else if (configured.Value <= TimeSpan.Zero)
        {
            throw new OptionsValidationException(nameof(CkMethodIdempotencyOptions), typeof(CkMethodIdempotencyOptions),
                [$"{CkMethodIdempotencyOptions.SectionName}:{nameof(CkMethodIdempotencyOptions.Ttl)} must be greater than zero."]);
        }
        else
        {
            Ttl = configured.Value;
        }
    }

    /// <summary>The lifetime of a record.</summary>
    public TimeSpan Ttl { get; }
}
