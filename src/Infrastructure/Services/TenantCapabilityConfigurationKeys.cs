namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     Keys of the per-tenant enabled flags the capability services persist through
///     <c>ITenantContext.SetConfigurationAsync</c> (value type <see cref="DefaultConfigurationEnabled" />).
///     This is the single source of truth for the key literals: the owning service's
///     <c>DefaultConfigurationCreatorService</c> passes the key to the Standardized base, and any
///     service that needs another service's flag (e.g. the tenant delete/detach guard in the asset
///     repository, AB#4255) reads it through <see cref="ITenantCapabilityStateReader" />.
/// </summary>
/// <remarks>
///     Stream data is deliberately not listed here: its flag is engine-owned
///     (<c>StreamDataConfigurationKeys.StreamDataEnabledKey</c>, value type
///     <c>StreamDataGlobalSettings</c>) and read through <c>ITenantContext.IsStreamDataEnabledAsync</c>.
///     Renaming any of these literals is a data migration for every existing tenant.
/// </remarks>
public static class TenantCapabilityConfigurationKeys
{
    /// <summary>
    ///     Enabled flag of the Communication Controller (adapters, pools, pipelines).
    /// </summary>
    public const string Communication = "CommunicationControllerServicesEnabled";

    /// <summary>
    ///     Enabled flag of the Reporting service.
    /// </summary>
    public const string Reporting = "ReportingServices";

    /// <summary>
    ///     Enabled flag of the AI service.
    /// </summary>
    public const string AiServices = "AiServicesEnabled";
}
