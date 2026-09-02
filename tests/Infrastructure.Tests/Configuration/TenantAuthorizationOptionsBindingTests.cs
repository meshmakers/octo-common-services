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
    ///     🔴 AB#5054: the user path's default is the <b>opposite</b> of the service path's. A host
    ///     where the user check is genuinely live today (octo-mcp-service, octo-ai-services since
    ///     AB#5056) must not be weakened by the staging option merely existing, and a host that
    ///     forgets to opt down must be closed rather than open. The zero enum value is the enforcing
    ///     one for the same reason, so even a default-constructed options object enforces.
    /// </summary>
    [Fact]
    public void WithoutRegistration_UserTokenPath_Enforces()
    {
        var provider = new ServiceCollection().AddOptions().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(UserTokenTenantEnforcementMode.Enforce, options.UserTokenEnforcement);
        Assert.Equal(UserTokenTenantEnforcementMode.Enforce, default(UserTokenTenantEnforcementMode));
    }

    /// <summary>
    ///     The operator knob for the AB#5054 migration, spelled the way it is set in Helm.
    /// </summary>
    [Fact]
    public void AddOctoTenantAuthorization_BindsUserTokenEnforcementFromEnvironmentVariable()
    {
        const string variable = "OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "LogOnly");

            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables("OCTO_")
                .Build();

            var provider = new ServiceCollection()
                .AddOctoTenantAuthorization(configuration)
                .BuildServiceProvider();

            var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

            Assert.Equal(UserTokenTenantEnforcementMode.LogOnly, options.UserTokenEnforcement);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    /// <summary>
    ///     A service may set the migration mode in code, and configuration must still win — that is
    ///     what lets an operator flip a service to <c>Enforce</c> without a release. Registration
    ///     order carries this: the code default is registered <b>before</b> the section binding.
    /// </summary>
    [Fact]
    public void ConfigurationOverridesTheCodeDefault_WhenRegisteredFirst()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(UserTokenTenantEnforcementMode.Enforce, options.UserTokenEnforcement);
    }

    /// <summary>
    ///     AB#5060: the cache TTL of the parent-tenant administration rule is the only knob that rule
    ///     has — its <i>scope</i> is the set of marked endpoints, never a flag — and it must arrive
    ///     with a value that keeps the hierarchy off the database on every request.
    /// </summary>
    [Fact]
    public void WithoutRegistration_TenantHierarchyCache_DefaultsToOneMinute()
    {
        var provider = new ServiceCollection().AddOptions().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(60), options.TenantHierarchyCacheDuration);
    }

    /// <summary>
    ///     …and it is settable per environment like every other value of the section.
    /// </summary>
    [Fact]
    public void AddOctoTenantAuthorization_BindsTheTenantHierarchyCacheDuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:TenantHierarchyCacheDuration"] = "00:05:00"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(5), options.TenantHierarchyCacheDuration);
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
