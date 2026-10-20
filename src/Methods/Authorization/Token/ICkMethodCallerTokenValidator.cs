using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     The <c>reason</c> values of the <c>METHOD_FORBIDDEN</c> answer that a refused caller token produces (additive
///     values of the existing <c>reason</c> parameter, kebab-case). They tell a client what to do next; they never name the
///     claim or value that failed.
/// </summary>
public static class CkMethodTokenReasons
{
    /// <summary>The request carries no token. Send the access token of the caller.</summary>
    public const string TokenMissing = "token-missing";

    /// <summary>The token is expired (beyond the clock skew) and otherwise valid. Retry with a fresh token.</summary>
    public const string TokenExpired = "token-expired";

    /// <summary>
    ///     The token is malformed, unsigned, signed by a foreign key, issued by a foreign issuer, lacks the required scope
    ///     or an identity (<c>sub</c> or <c>client_id</c>). Do not retry with the same token.
    /// </summary>
    public const string TokenInvalid = "token-invalid";

    /// <summary>The token was issued for another tenant than the request addresses.</summary>
    public const string TenantNotAllowed = "tenant-not-allowed";

    /// <summary>An audience is configured and the token carries none of them.</summary>
    public const string AudienceMismatch = "audience-mismatch";
}

/// <summary>Outcome of <see cref="ICkMethodCallerTokenValidator" />: a validated principal or a language-neutral failure.</summary>
public sealed class CkMethodTokenValidationResult
{
    private CkMethodTokenValidationResult(CkMethodPrincipal? principal, CkMethodFailure? failure)
    {
        Principal = principal;
        Failure = failure;
    }

    /// <summary><c>true</c> when the token is valid for the tenant; <see cref="Principal" /> is set.</summary>
    public bool IsValid => Principal is not null;

    /// <summary>The caller built from the validated token, or <c>null</c> when the token was refused.</summary>
    public CkMethodPrincipal? Principal { get; }

    /// <summary>
    ///     <c>METHOD_FORBIDDEN</c> with the <c>reason</c> parameter (one of <see cref="CkMethodTokenReasons" />), or
    ///     <c>null</c> when the token is valid. A handler returns it with <see cref="CkMethodOutcome.Fail(CkMethodFailure)" />.
    /// </summary>
    public CkMethodFailure? Failure { get; }

    /// <summary>The reason of <see cref="Failure" />, or <c>null</c> when the token is valid.</summary>
    public string? Reason => Failure is { } f && f.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var r) ? r : null;

    internal static CkMethodTokenValidationResult Valid(CkMethodPrincipal principal) => new(principal, null);

    internal static CkMethodTokenValidationResult Refused(string reason) => new(null,
        new CkMethodFailure(CkMethodErrorCodes.MethodForbidden,
            new Dictionary<string, string> { [CkMethodErrorParameters.Reason] = reason }));
}

/// <summary>
///     Re-validates the access token that a remote call forwards in <see cref="CkMethodRequest.CallerAccessToken" /> with
///     the settings of the hosting service's HTTP API, and returns the caller the handler may authorize on. The bus is not
///     authenticated per message: the <c>Caller</c> member of a request is informational and never a reason to trust an
///     identity.
/// </summary>
/// <remarks>
///     The handler host calls it before the handler runs when a service opted in with
///     <c>AddCkMethodCallerTokenValidation</c>; a handler author normally does not call it. It is public for hosts that
///     receive method requests outside the handler host (for example the pipeline handler). It never throws for a bad
///     token and never echoes the token or a claim value; it throws only for an infrastructure problem (the identity keys
///     cannot be loaded), which the handler host answers as <c>METHOD_HANDLER_FAILED</c>.
/// </remarks>
public interface ICkMethodCallerTokenValidator
{
    /// <summary>Validates <paramref name="accessToken" /> for the tenant <paramref name="expectedTenantId" />.</summary>
    /// <param name="accessToken">The raw JWT; <c>null</c> or empty gives <see cref="CkMethodTokenReasons.TokenMissing" />.</param>
    /// <param name="expectedTenantId">The tenant of the request (<c>CkMethodRequest.TenantId</c>).</param>
    /// <param name="cancellationToken">Cancels the loading of the signing keys.</param>
    ValueTask<CkMethodTokenValidationResult> ValidateAsync(string? accessToken, string expectedTenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>Convenience overloads of <see cref="ICkMethodCallerTokenValidator" />.</summary>
public static class CkMethodCallerTokenValidatorExtensions
{
    /// <summary>Validates the forwarded token of <paramref name="request" /> for the tenant of the request.</summary>
    public static ValueTask<CkMethodTokenValidationResult> ValidateAsync(this ICkMethodCallerTokenValidator validator,
        CkMethodRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(request);
        return validator.ValidateAsync(request.CallerAccessToken, request.TenantId, cancellationToken);
    }
}
