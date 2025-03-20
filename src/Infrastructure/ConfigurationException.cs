using Meshmakers.Octo.Services.Common;

namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
/// Used to indicate that a configuration error occurred.
/// </summary>
public class ConfigurationException : OctoServiceException
{
    private ConfigurationException()
    {
    }

    private ConfigurationException(string message) : base(message)
    {
    }

    private ConfigurationException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception UnableToRetrieveConfiguration(string tenantId, string configurationName, Exception exception)
    {
        return new ConfigurationException($"Unable to retrieve configuration '{configurationName}' for tenant '{tenantId}'", exception);
    }

    public static Exception ConfigurationNotFound(string tenantId, string configurationName)
    {
        return new ConfigurationException($"Configuration '{configurationName}' not found for tenant '{tenantId}'");
    }

    internal static Exception TenantAlreadyEnabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' is already enabled.");
    }

    internal static Exception TenantAlreadyDisabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' is already disabled.");
    }

    internal static Exception TenantCannotBeEnabledDisabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' cannot be enabled/disabled because there is no schema defined.");
    }

    public static Exception TenantIsAutoEnabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' is auto enabled.");
    }
}
