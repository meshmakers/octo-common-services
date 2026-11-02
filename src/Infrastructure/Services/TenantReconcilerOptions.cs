namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Tunables of the stalled-tenant reconciler (AB#4348, AB#8117). Bound from the configuration section
///     <see cref="SectionName" /> (<c>OCTO_TENANTRECONCILER__*</c>) by <c>AddOctoServiceInfrastructure</c>.
/// </summary>
public class TenantReconcilerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "TenantReconciler";

    /// <summary>
    ///     The process-wide effective options. The reconciler lives in a scoped service whose constructor
    ///     cannot take new dependencies without touching every derived service, so it reads them from here;
    ///     <c>AddOctoServiceInfrastructure</c> keeps this in sync with the bound configuration.
    /// </summary>
    public static TenantReconcilerOptions Current { get; internal set; } = new();

    /// <summary>
    ///     A <c>Creating</c> tenant is claimed by the reconciler only once its creation cycle is at least this old.
    ///     A brand-new tenant is still driven by the normal setup path (which finishes within seconds); claiming it
    ///     earlier raced that path, failed on "not seeded yet" and parked the tenant behind the reconcile lease
    ///     (AB#8117). Default 30 s; zero disables the filter.
    /// </summary>
    public TimeSpan MinimumAge { get; set; } = TimeSpan.FromSeconds(30);
}
