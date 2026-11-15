using System.Globalization;
using System.Security.Claims;

namespace Meshmakers.Octo.Services.Infrastructure.TenantGeneration;

/// <summary>
///     How the tenant generation check (AB#8999, security item AB#8970) treats an access token whose <c>tenant_gen</c>
///     claim is missing or names an earlier tenant with the same id. Option
///     <c>TenantAuthorization:TenantGenerationEnforcement</c>
///     (env <c>OCTO_TenantAuthorization__TenantGenerationEnforcement</c>).
/// </summary>
public enum TenantGenerationEnforcementMode
{
    /// <summary>No lookup, no log, no metric: the check does not run.</summary>
    Disabled = 0,

    /// <summary>
    ///     <b>Default in this release.</b> The request outcome never changes. <c>missing</c> and <c>mismatch</c> are
    ///     logged (Warning, rate-limited per client) and counted; a failed lookup is counted and the request proceeds.
    /// </summary>
    LogOnly = 1,

    /// <summary>
    ///     <c>missing</c> or <c>mismatch</c> for a tenant that has a generation is refused (HTTP 403,
    ///     <c>METHOD_FORBIDDEN</c>); a failed lookup is answered HTTP 503 / a method error, never 200.
    /// </summary>
    Enforce = 2
}

/// <summary>The result of evaluating a token's <c>tenant_id</c> and <c>tenant_gen</c> (AB#8999).</summary>
public enum TenantGenerationOutcome
{
    /// <summary><c>tenant_gen</c> equals the current generation of the token tenant.</summary>
    Match,

    /// <summary>The token tenant has a generation, the token carries no <c>tenant_gen</c>.</summary>
    Missing,

    /// <summary>
    ///     <c>tenant_gen</c> differs from the current generation, or is not a canonical string (not a string, more than one
    ///     value, sign, leading zero, whitespace, decimal point, exponent).
    /// </summary>
    Mismatch,

    /// <summary>
    ///     Not checked: no token tenant, the system tenant, a tenant without lifecycle record or <c>CreatedUtc</c>
    ///     (legacy), or a host without the lifecycle store.
    /// </summary>
    NotChecked,

    /// <summary>The current generation could not be read.</summary>
    LookupFailed
}

/// <summary>What the caller of <see cref="ITenantGenerationCheck.CheckAsync" /> does with the request.</summary>
public enum TenantGenerationDecision
{
    /// <summary>Continue as without the check.</summary>
    Allow,

    /// <summary>Refuse: HTTP 403, <c>METHOD_FORBIDDEN</c>, <c>invalid_grant</c>.</summary>
    Deny,

    /// <summary>The generation cannot be verified (Enforce only): HTTP 503, a method error, <c>server_error</c>.</summary>
    Unavailable
}

/// <summary>Outcome and decision of one check.</summary>
/// <param name="Outcome">The evaluation.</param>
/// <param name="Decision">What the caller does, after the mode was applied.</param>
public sealed record TenantGenerationCheckResult(TenantGenerationOutcome Outcome, TenantGenerationDecision Decision);

/// <summary>
///     The input of one check. Built by the caller from the bearer access-token principal; never from an ID token or a
///     cookie principal.
/// </summary>
/// <param name="TokenTenantId">The token's <c>tenant_id</c> — the tenant the generation is looked up for.</param>
/// <param name="Generation">The token's <c>tenant_gen</c>; <c>null</c> when the token carries none.</param>
/// <param name="Transport">
///     Metric label <c>transport</c>: <see cref="TenantGenerationTransports.Http" />,
///     <see cref="TenantGenerationTransports.Method" /> or <see cref="TenantGenerationTransports.Grant" />.
/// </param>
/// <param name="ClientId">The token's <c>client_id</c> (metric label and log, bounded).</param>
/// <param name="GrantType">
///     Optional metric label <c>grant_type</c> (identity, AB#9017); only added when set. Use a fixed vocabulary.
/// </param>
public sealed record TenantGenerationCheckRequest(
    string? TokenTenantId,
    TenantGenerationClaim? Generation,
    string Transport,
    string? ClientId,
    string? GrantType = null);

/// <summary>The fixed values of the metric label <c>transport</c>.</summary>
public static class TenantGenerationTransports
{
    /// <summary><c>TenantAuthorizationMiddleware</c>.</summary>
    public const string Http = "http";

    /// <summary>CK method calls (bus host and in-process transport).</summary>
    public const string Method = "method";

    /// <summary>Identity grants (token exchange, on-behalf-of, refresh, codes; AB#9017).</summary>
    public const string Grant = "grant";
}

/// <summary>
///     The <c>tenant_gen</c> claim of an access token as the check sees it. Holds the value only to compare it; the value
///     is never logged, counted or written into a response (a record for value equality; its printed form shows
///     <see cref="IsWellFormed" /> only).
/// </summary>
public sealed record TenantGenerationClaim
{
    /// <summary>The claim type identity issues (AB#8997).</summary>
    public const string ClaimType = "tenant_gen";

    private TenantGenerationClaim(string? value, bool isWellFormed)
    {
        Value = value;
        IsWellFormed = isWellFormed;
    }

    /// <summary>The value when <see cref="IsWellFormed" />, otherwise <c>null</c>.</summary>
    internal string? Value { get; }

    /// <summary>
    ///     <c>true</c> for exactly one claim of value type string in the canonical form of
    ///     <see cref="TenantGenerationFormat.Format" />.
    /// </summary>
    public bool IsWellFormed { get; }

    /// <summary>
    ///     Reads <c>tenant_gen</c> from a bearer access-token principal; <c>null</c> when the token carries none. More than
    ///     one value, a value type other than string (a JSON number) or a non-canonical string give a claim that is not
    ///     well formed, which the check treats as a mismatch.
    /// </summary>
    public static TenantGenerationClaim? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        var claims = principal.FindAll(ClaimType).ToList();
        if (claims.Count == 0)
        {
            return null;
        }

        if (claims.Count > 1)
        {
            return new TenantGenerationClaim(null, false);
        }

        var claim = claims[0];
        return FromValue(claim.Value, string.Equals(claim.ValueType, ClaimValueTypes.String, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A claim from a value already taken from a token. <paramref name="isString" /> is <c>false</c> when the token
    ///     carried the value as anything but a JSON string.
    /// </summary>
    public static TenantGenerationClaim FromValue(string? value, bool isString = true)
    {
        return isString && TenantGenerationFormat.IsCanonical(value)
            ? new TenantGenerationClaim(value, true)
            : new TenantGenerationClaim(null, false);
    }
}

/// <summary>
///     The one format of a tenant generation, identical to <c>OctoTenantGenerationClaimHandler.FormatGeneration</c>
///     (octo-identity-services, AB#8997, a27681eb): <c>CreatedUtc</c> normalised to UTC (<c>Utc</c> as is, <c>Local</c>
///     converted, <c>Unspecified</c> taken as UTC), Unix milliseconds by integer truncation, invariant decimal string.
///     Shared test vector: <c>2026-11-15T13:46:21.540Z</c> → <c>"1794750381540"</c>.
/// </summary>
public static class TenantGenerationFormat
{
    // long.MaxValue has 19 digits; a generation never needs more.
    private const int MaxDigits = 19;

    /// <summary>The generation of a lifecycle <c>CreatedUtc</c>, or <c>null</c> when it is <c>default</c>.</summary>
    public static string? Format(DateTime createdUtc)
    {
        if (createdUtc == default)
        {
            return null;
        }

        var utc = createdUtc.Kind switch
        {
            DateTimeKind.Utc => createdUtc,
            DateTimeKind.Local => createdUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc)
        };

        // Integer division of the ticks since the epoch: truncation, never rounding.
        var milliseconds = (utc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
        return milliseconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     <c>true</c> when <paramref name="value" /> is what <see cref="Format" /> can produce: ASCII digits only, no sign,
    ///     no leading zero (except <c>"0"</c>), no whitespace, decimal point or exponent, at most 19 digits.
    /// </summary>
    public static bool IsCanonical(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxDigits)
        {
            return false;
        }

        if (value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>The numeric value of a canonical generation; <c>false</c> for anything else.</summary>
    internal static bool TryParse(string? value, out long milliseconds)
    {
        milliseconds = 0;
        return IsCanonical(value) &&
               long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);
    }
}
