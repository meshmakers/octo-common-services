using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Services.Methods.Idempotency;

/// <summary>
///     One key of the parameters fingerprint (OD-4 G1, AB#7819): a public id and a secret. The id is stored with the
///     fingerprint; the secret never leaves the process. <see cref="ToString" /> prints no secret.
/// </summary>
public sealed class CkMethodFingerprintKeyOptions
{
    /// <summary>Public name of the key (1 to 64 characters of <c>A-Z a-z 0-9 . _ -</c>); stored with every fingerprint.</summary>
    public string? Id { get; set; }

    /// <summary>
    ///     The key material: base64 of at least 32 random bytes (for example <c>openssl rand -base64 32</c>). A secret of
    ///     the environment (Vault + Helm secret value), never a value of a checked-in file.
    /// </summary>
    public string? Secret { get; set; }

    internal bool IsEmpty => string.IsNullOrWhiteSpace(Id) && string.IsNullOrWhiteSpace(Secret);

    /// <summary>Prints the id, never the secret.</summary>
    public override string ToString() => $"{nameof(CkMethodFingerprintKeyOptions)} {{ Id = {Id}, Secret = [redacted] }}";
}

/// <summary>
///     Keys of the parameters fingerprint (configuration section <c>Methods:Idempotency:Fingerprint</c>). All replicas of a
///     service must share the same keys, otherwise a repeat that lands on another replica looks like changed parameters.
///     Rotation: move the old <see cref="Current" /> to <see cref="Previous" /> and set a new <see cref="Current" /> (new id);
///     a fingerprint stored under the previous key still verifies. Keys are read at the start of the service.
/// </summary>
public sealed class CkMethodFingerprintOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodIdempotencyOptions.SectionName + ":Fingerprint";

    /// <summary>The key that makes new fingerprints. Without it, a keyed call to a method with sensitive parameters is refused.</summary>
    public CkMethodFingerprintKeyOptions Current { get; set; } = new();

    /// <summary>The key of the rotation before; only used to verify fingerprints that are already stored.</summary>
    public CkMethodFingerprintKeyOptions Previous { get; set; } = new();

    /// <summary>Prints no secret.</summary>
    public override string ToString() =>
        $"{nameof(CkMethodFingerprintOptions)} {{ Current = {Current}, Previous = {Previous} }}";
}

/// <summary>
///     Fails the start of the service when the fingerprint keys are half-configured or malformed. The messages name the
///     setting, never the value.
/// </summary>
internal sealed partial class CkMethodFingerprintOptionsValidator : IValidateOptions<CkMethodFingerprintOptions>
{
    internal const int MinSecretBytes = 32;
    internal const int MaxIdLength = 64;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();

    public ValidateOptionsResult Validate(string? name, CkMethodFingerprintOptions options)
    {
        var failures = new List<string>();
        CheckKey("Current", options.Current, failures);
        CheckKey("Previous", options.Previous, failures);
        if (!options.Previous.IsEmpty && options.Current.IsEmpty)
        {
            failures.Add($"{CkMethodFingerprintOptions.SectionName}:Previous needs {CkMethodFingerprintOptions.SectionName}:Current.");
        }

        if (!options.Previous.IsEmpty && !options.Current.IsEmpty &&
            string.Equals(options.Previous.Id, options.Current.Id, StringComparison.Ordinal))
        {
            failures.Add($"{CkMethodFingerprintOptions.SectionName}:Previous:Id must differ from Current:Id.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckKey(string slot, CkMethodFingerprintKeyOptions key, List<string> failures)
    {
        if (key.IsEmpty)
        {
            return;
        }

        var prefix = $"{CkMethodFingerprintOptions.SectionName}:{slot}";
        if (string.IsNullOrWhiteSpace(key.Id) || key.Id.Length > MaxIdLength || !IdPattern().IsMatch(key.Id))
        {
            failures.Add($"{prefix}:Id must be 1 to {MaxIdLength} characters of A-Z a-z 0-9 . _ -.");
        }

        if (string.IsNullOrWhiteSpace(key.Secret))
        {
            failures.Add($"{prefix}:Secret is missing.");
        }
        else if (!TryDecode(key.Secret, out var bytes) || bytes.Length < MinSecretBytes)
        {
            failures.Add($"{prefix}:Secret must be base64 of at least {MinSecretBytes} bytes.");
        }
    }

    internal static bool TryDecode(string secret, out byte[] bytes)
    {
        bytes = [];
        var buffer = new byte[secret.Length];
        if (!Convert.TryFromBase64String(secret.Trim(), buffer, out var written))
        {
            return false;
        }

        bytes = buffer.AsSpan(0, written).ToArray();
        return true;
    }
}
