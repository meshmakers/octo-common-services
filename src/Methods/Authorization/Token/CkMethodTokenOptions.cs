namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     Options of the caller token validation for remote method handlers (configuration section
///     <c>Methods:Token</c>). The defaults mirror the HTTP API of the hosting service: the same authority and issuer, the
///     identity JWKS, the scope <c>octo_api</c>, the tenant rule of <c>TenantAuthorizationMiddleware</c> and no audience
///     validation (<c>ValidateAudience</c> is off platform-wide).
/// </summary>
public sealed class CkMethodTokenOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName + ":Token";

    /// <summary>The scope every caller token must carry, like the HTTP API (<c>octo_api</c>).</summary>
    public const string DefaultRequiredScope = "octo_api";

    /// <summary>Name of the <see cref="System.Net.Http.HttpClient" /> that downloads the discovery document and the JWKS.</summary>
    public const string HttpClientName = "Meshmakers.Octo.Methods.TokenKeys";

    /// <summary>
    ///     Base address of the identity service, the same value the host's HTTP API uses as authority. Required (unless
    ///     <see cref="DisableValidation" /> is set in a development environment). A trailing slash is added: tokens of
    ///     the identity service carry <c>iss</c> with a trailing slash.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    ///     Issuer a token must carry. Empty means <see cref="Authority" /> (with trailing slash), as in the HTTP API.
    /// </summary>
    public string? ValidIssuer { get; set; }

    /// <summary>
    ///     Address of the OpenID Connect discovery document. Empty means
    ///     <c>{Authority}.well-known/openid-configuration</c>.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    ///     Audiences of which a token must carry one. <b>Empty (the default) means the audience is not validated</b>,
    ///     exactly like the HTTP API: with the setting empty, every <c>octo_api</c> token of the tenant is accepted. Set it
    ///     as soon as the identity service issues audience-scoped tokens.
    /// </summary>
    public IList<string> ValidAudiences { get; set; } = [];

    /// <summary>
    ///     The scope a token must carry. Empty switches the scope check off (not recommended); default
    ///     <see cref="DefaultRequiredScope" />.
    /// </summary>
    public string? RequiredScope { get; set; } = DefaultRequiredScope;

    /// <summary>Tolerated clock difference for the expiry and not-before check. Default 30 s, like the platform.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Signature algorithms that are accepted. Empty (the default) means the asymmetric algorithms of
    ///     <see cref="DefaultAlgorithms" />; <c>none</c> and symmetric (<c>HS*</c>) algorithms can never be accepted.
    /// </summary>
    public IList<string> ValidAlgorithms { get; set; } = [];

    /// <summary>Asymmetric algorithms accepted when <see cref="ValidAlgorithms" /> is empty.</summary>
    public static IReadOnlyList<string> DefaultAlgorithms { get; } =
        ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    /// <summary>
    ///     Accepted values of the JOSE <c>typ</c> header (for example <c>at+jwt</c>). Empty (the default) means the type is
    ///     not checked, like the HTTP API.
    /// </summary>
    public IList<string> ValidTypes { get; set; } = [];

    /// <summary>
    ///     How often the signing keys are refreshed on a schedule. Default 12 hours (the default of the ASP.NET Core JWT
    ///     bearer handler). The minimum of the underlying library is 5 minutes.
    /// </summary>
    public TimeSpan KeyAutomaticRefreshInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    ///     Shortest time between two forced refreshes. A token that is signed with an unknown key id triggers one forced
    ///     refresh (key rotation) and is then validated again; this setting keeps a flood of forged key ids from hammering
    ///     the identity service. Default 5 minutes.
    /// </summary>
    public TimeSpan KeyRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Require HTTPS for the discovery document and the JWKS. Default <c>true</c>; switch off only for a local identity
    ///     service without a certificate.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    ///     <b>Test configuration only.</b> Skips issuer, signature, expiry and audience validation; the claims are read
    ///     from the unverified token (the tenant and scope rules still apply). The host fails to start unless it runs in the
    ///     <c>Development</c> environment. Never switch it on to "make a call work".
    /// </summary>
    public bool DisableValidation { get; set; }
}
