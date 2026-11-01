namespace Meshmakers.Octo.Services.Infrastructure.Cors;

/// <summary>
///     Service-specific additions to the shared known-origins CORS policy built by the
///     <see cref="Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider" /> of
///     <c>AddOctoServiceInfrastructure()</c>.
/// </summary>
/// <remarks>
///     AB#7270: the allowed origins always come from the registered client origins of the tenant
///     (<see cref="IKnownOriginsProvider" />) — these options can neither widen the origin list nor
///     enable credentials. They only add response headers a browser may read (e.g. the tus upload
///     headers of bot-services) and the preflight cache lifetime.
/// </remarks>
public sealed class OctoCorsOptions
{
    /// <summary>
    ///     Response headers exposed to scripts of an allowed origin (<c>Access-Control-Expose-Headers</c>).
    /// </summary>
    public IList<string> ExposedHeaders { get; } = new List<string>();

    /// <summary>
    ///     Lifetime of a preflight result in the browser cache (<c>Access-Control-Max-Age</c>);
    ///     <c>null</c> sends no header.
    /// </summary>
    public TimeSpan? PreflightMaxAge { get; set; }
}
