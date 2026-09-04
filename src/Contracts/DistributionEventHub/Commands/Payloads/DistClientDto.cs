namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

/// <summary>
///     Represents a client.
/// </summary>
/// <param name="ClientId">Client id.</param>
/// <param name="ClientName">Client name.</param>
/// <param name="ClientUri">Client uri.</param>
// ReSharper disable once ClassNeverInstantiated.Global
public record DistClientDto(string ClientId, string ClientName, string ClientUri)
{
    /// <summary>
    ///     Gets or sets allowed grant types.
    /// </summary>
    public string[] AllowedGrantTypes { get; init; } = null!;

    /// <summary>
    ///     Gets or sets if a consent is required.
    /// </summary>
    public bool RequireConsent { get; init; }

    /// <summary>
    ///     Gets or sets redirect uris.
    /// </summary>
    public string[] RedirectUris { get; init; } = null!;

    /// <summary>
    ///     Gets or sets post logout redirect uris.
    /// </summary>
    public string[] PostLogoutRedirectUris { get; init; } = null!;

    /// <summary>
    ///     Gets or sets allowed cors origins.
    /// </summary>
    public string[] AllowedCorsOrigins { get; init; } = null!;

    /// <summary>
    ///     Gets or sets allowed scopes.
    /// </summary>
    public string[] AllowedScopes { get; init; } = null!;

    /// <summary>
    ///     Gets or sets if offline access is allowed.
    /// </summary>
    public bool AllowOfflineAccess { get; init; }

    /// <summary>
    ///     Gets or sets the front-channel logout URI for Single Logout (SLO).
    /// </summary>
    public string? FrontChannelLogoutUri { get; init; }

    /// <summary>
    ///     Gets or sets whether session ID is required for front-channel logout.
    /// </summary>
    public bool FrontChannelLogoutSessionRequired { get; init; } = true;

    /// <summary>
    ///     Gets or sets the back-channel logout URI for Single Logout (SLO).
    /// </summary>
    public string? BackChannelLogoutUri { get; init; }

    /// <summary>
    ///     Gets or sets whether session ID is required for back-channel logout.
    /// </summary>
    public bool BackChannelLogoutSessionRequired { get; init; } = true;

    /// <summary>
    ///     Optional <b>plaintext</b> client secret (AB#5027). The identity service hashes it
    ///     (SHA-256, the Duende shared-secret convention) before it is stored — the plaintext is never
    ///     persisted on the identity side. Leave <c>null</c> for a public client; that is the historic
    ///     behaviour of every producer that predates this property.
    ///     <para>
    ///     🔴 Never log this value, in no log level and not truncated. <see cref="ToString" /> is
    ///     overridden on this record for exactly that reason — the compiler-generated record
    ///     <c>ToString()</c> prints every property, so a single <c>LogDebug("{Dto}", dto)</c> would
    ///     leak the secret into the log pipeline.
    ///     </para>
    ///     <para>
    ///     Idempotency contract: a producer must only send a secret when it actually intends to
    ///     (re-)issue one. The consumer overwrites the client's secret whenever this is non-empty and
    ///     preserves the existing one whenever it is null — so "send nothing" is the no-rotation path.
    ///     </para>
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    ///     Gets or sets whether the client must authenticate with a secret (AB#5027). Defaults to
    ///     <c>false</c>, which is what the consumer hard-coded before this property existed, so
    ///     every pre-existing producer keeps creating public clients unchanged. Set it to
    ///     <c>true</c> together with <see cref="ClientSecret" /> for a <c>client_credentials</c>
    ///     service account — a secretless client with that grant would hand a token to anyone who
    ///     knows the client id.
    /// </summary>
    public bool RequireClientSecret { get; init; }

    /// <summary>
    ///     Optional role names to assign to the client through the identity <c>AssignedRole</c>
    ///     association (AB#5027). Applied additively and idempotently: roles already assigned are
    ///     left alone, roles not listed here are <b>not</b> removed, and an unknown role name is
    ///     skipped with a warning rather than failing the whole identity-data setup.
    ///     <para>
    ///     Before this property there was no bus path at all for client roles — the only ways were
    ///     the identity REST API (which needs an OAuth token, i.e. an identity the caller does not
    ///     have yet at provisioning time) or a blueprint seed.
    ///     </para>
    /// </summary>
    public string[]? AssignedRoleNames { get; init; }

    /// <summary>
    ///     Optional client ids of ACTOR clients that may impersonate <b>this</b> client (AB#5114):
    ///     for every named client id that resolves in the tenant, the identity service materialises
    ///     a <c>System.Identity/MayActAs</c> association actor→this-client, which is what the
    ///     impersonation grant and the on-behalf-of <c>requested_client_id</c> extension authorize
    ///     against — so an adapter can obtain its pipeline service account's identity without ever
    ///     holding the service account's secret.
    ///     <para>
    ///     Applied additively and idempotently, exactly like the pre-AB#5111 role semantics:
    ///     edges already present are left alone, edges to actors not listed here are <b>not</b>
    ///     removed (v1 — removal stays a manual/operator concern), and an actor client id that does
    ///     not (yet) exist in the tenant is skipped with a warning rather than failing the whole
    ///     identity-data setup (seed/provisioning ordering — the actor may arrive on a later pass).
    ///     <c>null</c> (the default, and what every producer that predates this property sends)
    ///     changes nothing.
    ///     </para>
    /// </summary>
    public IList<string>? MayActAsClientIds { get; init; }

    /// <summary>
    ///     Redacting override. The compiler-generated record <c>ToString()</c> prints every property
    ///     including <see cref="ClientSecret" />; a structured-logging call that passes the DTO as a
    ///     single argument would therefore write a live client secret into the logs. Only the client
    ///     id is safe to print, and it is the only field worth printing for diagnostics.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(DistClientDto)} {{ {nameof(ClientId)} = {ClientId} }}";
    }
}