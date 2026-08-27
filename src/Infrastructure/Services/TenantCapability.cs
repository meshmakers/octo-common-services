namespace Meshmakers.Octo.Services.Infrastructure.Services;

/// <summary>
///     The per-tenant capabilities an operator can enable and disable. Each one is backed by an enabled
///     flag stored as an <c>RtTenantConfiguration</c> document in the tenant's own database (AB#4255).
///     The declaration order is the display order used when several capabilities are listed.
/// </summary>
public enum TenantCapability
{
    /// <summary>
    ///     Stream data (time-series archives). Flag key: the engine-owned
    ///     <c>StreamDataConfigurationKeys.StreamDataEnabledKey</c>; disabling keeps the key with
    ///     <c>IsEnabled = false</c>.
    /// </summary>
    StreamData,

    /// <summary>
    ///     Communication (adapters, pools, pipelines). Flag key:
    ///     <see cref="TenantCapabilityConfigurationKeys.Communication" />; disabling deletes the key.
    /// </summary>
    Communication,

    /// <summary>
    ///     Reporting. Flag key: <see cref="TenantCapabilityConfigurationKeys.Reporting" />; disabling
    ///     deletes the key.
    /// </summary>
    Reporting,

    /// <summary>
    ///     AI services. Flag key: <see cref="TenantCapabilityConfigurationKeys.AiServices" />; disabling
    ///     deletes the key.
    /// </summary>
    AiServices,
}

/// <summary>
///     Extensions for <see cref="TenantCapability" />
/// </summary>
public static class TenantCapabilityExtensions
{
    /// <summary>
    ///     Returns the operator-facing name of the capability, as used in error messages and logs.
    /// </summary>
    public static string DisplayName(this TenantCapability capability)
    {
        return capability switch
        {
            TenantCapability.StreamData => "Stream Data",
            TenantCapability.Communication => "Communication",
            TenantCapability.Reporting => "Reporting",
            TenantCapability.AiServices => "AI Services",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
        };
    }
}
