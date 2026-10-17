namespace Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

/// <summary>
///     Informational description of the caller of a CK method (built from the gateway's claims).
///     Handlers must not trust it for authorization decisions.
/// </summary>
public record CkMethodCaller
{
    /// <summary>
    ///     Subject id (<c>sub</c>) of the user, <c>null</c> for client-credentials callers.
    /// </summary>
    public string? SubjectId { get; init; }

    /// <summary>
    ///     OAuth client id the token was issued to.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    ///     Tenant id claim of the token.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    ///     Display name of the caller.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Role claims of the caller.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}
