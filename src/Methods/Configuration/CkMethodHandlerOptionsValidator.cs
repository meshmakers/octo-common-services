using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     Fails the start of a handler host (<c>ValidateOnStart</c>) when the announce timing would let the registry of a
///     dispatcher drop live handlers or would crash the heartbeat loop (AB#7838): <c>Methods:HeartbeatInterval</c> at least
///     <see cref="CkMethodHandlerOptions.MinHeartbeatInterval" />, <c>Methods:Ttl</c> at least
///     <see cref="CkMethodHandlerOptions.MinTtlToHeartbeatRatio" /> times the interval (two lost heartbeats are tolerated) and
///     at most <see cref="CkMethodHandlerOptions.MaxTtl" /> (the clamp of the registry). The message names the key and the rule.
/// </summary>
internal sealed class CkMethodHandlerOptionsValidator : IValidateOptions<CkMethodHandlerOptions>
{
    public ValidateOptionsResult Validate(string? name, CkMethodHandlerOptions options)
    {
        var failures = new List<string>();
        var interval = options.HeartbeatInterval;
        var ttl = options.Ttl;
        var intervalKey = $"{CkMethodHandlerOptions.SectionName}:{nameof(CkMethodHandlerOptions.HeartbeatInterval)}";
        var ttlKey = $"{CkMethodHandlerOptions.SectionName}:{nameof(CkMethodHandlerOptions.Ttl)}";

        if (interval < CkMethodHandlerOptions.MinHeartbeatInterval)
        {
            failures.Add($"{intervalKey} is {interval} but must be at least {CkMethodHandlerOptions.MinHeartbeatInterval} " +
                         $"(default {TimeSpan.FromSeconds(15)}).");
        }

        if (ttl > CkMethodHandlerOptions.MaxTtl)
        {
            failures.Add($"{ttlKey} is {ttl} but must be at most {CkMethodHandlerOptions.MaxTtl} " +
                         "(the registry of a dispatcher clamps longer values).");
        }

        // Only judged against a usable interval: an invalid interval is already reported above.
        if (interval >= CkMethodHandlerOptions.MinHeartbeatInterval &&
            ttl < interval * CkMethodHandlerOptions.MinTtlToHeartbeatRatio)
        {
            failures.Add($"{ttlKey} is {ttl} but must be at least {CkMethodHandlerOptions.MinTtlToHeartbeatRatio} x " +
                         $"{intervalKey} ({interval * CkMethodHandlerOptions.MinTtlToHeartbeatRatio}), " +
                         "otherwise a live handler expires between two heartbeats (default " +
                         $"{TimeSpan.FromSeconds(45)}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
