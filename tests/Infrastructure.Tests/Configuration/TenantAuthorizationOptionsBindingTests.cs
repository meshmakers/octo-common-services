using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Tests.Configuration;

/// <summary>
///     Tests the wiring contract of <see cref="TenantAuthorizationOptions" /> (AB#5032 / AB#5047):
///     the defaults a service gets when it only calls <c>UseOctoTenantAuthorization()</c>, and the
///     single configuration section / environment-variable name every consumer must read.
/// </summary>
/// <remarks>
///     AB#5047: asset-repo, bot-services and MCP called <c>UseOctoTenantAuthorization()</c> without
///     ever calling <c>AddOctoTenantAuthorization(configuration)</c>, so their enforcement mode was
///     not settable per environment — an estate-wide switch to
///     <see cref="ServiceTokenTenantEnforcementMode.Enforce" /> would have left them behind silently.
///     These tests pin the two properties that make a fleet-wide switch possible at all: the
///     unregistered default must be today's behaviour, and the section/variable name must be the same
///     everywhere (it is a <c>const</c> here precisely so no consumer can drift).
/// </remarks>
public class TenantAuthorizationOptionsBindingTests
{
    /// <summary>
    ///     A service that never registers the options must still resolve them, and must get the
    ///     pre-AB#5032 request behaviour plus the audit log — never a mode that silently starts
    ///     refusing requests.
    /// </summary>
    [Fact]
    public void WithoutRegistration_ResolvesDefaults_LogOnlyAndEmptyAllowList()
    {
        var provider = new ServiceCollection().AddOptions().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(ServiceTokenTenantEnforcementMode.LogOnly, options.ServiceTokenEnforcement);
        Assert.Empty(options.CrossTenantServiceClientIds);
    }

    /// <summary>
    ///     The section name is part of the platform contract — every service binds
    ///     <c>TenantAuthorization</c>, so one operator-set value reaches all of them.
    /// </summary>
    [Fact]
    public void SectionName_IsTenantAuthorization()
    {
        Assert.Equal("TenantAuthorization", TenantAuthorizationOptions.SectionName);
    }

    /// <summary>
    ///     Binding from the configuration section, including the allow-list array.
    /// </summary>
    [Fact]
    public void AddOctoTenantAuthorization_BindsFromConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:ServiceTokenEnforcement"] = "Enforce",
                ["TenantAuthorization:CrossTenantServiceClientIds:0"] = "octo-fanout-client"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(ServiceTokenTenantEnforcementMode.Enforce, options.ServiceTokenEnforcement);
        Assert.Equal(new[] { "octo-fanout-client" }, options.CrossTenantServiceClientIds);
    }

    /// <summary>
    ///     The documented operator knob is the environment variable
    ///     <c>OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT</c>: every OctoMesh service adds
    ///     <c>AddEnvironmentVariables("OCTO_")</c> to its configuration, so this test pins the exact
    ///     spelling an operator sets in Helm.
    /// </summary>
    [Fact]
    public void AddOctoTenantAuthorization_BindsFromOctoPrefixedEnvironmentVariable()
    {
        const string variable = "OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT";
        const string listVariable = "OCTO_TENANTAUTHORIZATION__CROSSTENANTSERVICECLIENTIDS__0";
        var previous = Environment.GetEnvironmentVariable(variable);
        var previousList = Environment.GetEnvironmentVariable(listVariable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "Enforce");
            Environment.SetEnvironmentVariable(listVariable, "octo-fanout-client");

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables("OCTO_")
                .Build();

            var provider = new ServiceCollection()
                .AddOctoTenantAuthorization(configuration)
                .BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

            Assert.Equal(ServiceTokenTenantEnforcementMode.Enforce, options.ServiceTokenEnforcement);
            Assert.Equal(new[] { "octo-fanout-client" }, options.CrossTenantServiceClientIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Environment.SetEnvironmentVariable(listVariable, previousList);
        }
    }

    /// <summary>
    ///     An unset variable must leave the default intact — an operator who only sets the allow-list
    ///     must not accidentally end up enforcing.
    /// </summary>
    [Fact]
    public void AddOctoTenantAuthorization_WithoutConfiguredValues_KeepsDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(ServiceTokenTenantEnforcementMode.LogOnly, options.ServiceTokenEnforcement);
        Assert.Empty(options.CrossTenantServiceClientIds);
    }
}
