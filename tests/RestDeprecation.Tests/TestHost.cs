using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace RestDeprecation.Tests;

/// <summary>Claim values the test caller carries; none of them may ever appear in a metric label.</summary>
internal static class TestCaller
{
    public const string TenantId = "tenant-alpha-7";
    public const string UserName = "jane.doe@example.com";
    public const string Subject = "sub-8f2c4e10";
    public const string ClientId = "octo-cli";

    /// <summary>Header that makes <see cref="TestAuthHandler" /> authenticate the request.</summary>
    public const string AuthenticateHeader = "X-Test-Authenticate";

    /// <summary>Header carrying the role of the authenticated test caller.</summary>
    public const string RoleHeader = "X-Test-Role";
}

/// <summary>Authenticates a request that carries <see cref="TestCaller.AuthenticateHeader" />.</summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(TestCaller.AuthenticateHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new("sub", TestCaller.Subject),
            new("name", TestCaller.UserName),
            new("tenant_id", TestCaller.TenantId),
            new("client_id", TestCaller.ClientId)
        };
        if (Request.Headers.TryGetValue(TestCaller.RoleHeader, out var role))
        {
            claims.Add(new Claim("role", role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Restricts MVC to the given controller types, so every test host sees exactly its own controllers.</summary>
internal sealed class SelectedControllersFeatureProvider(IReadOnlyCollection<Type> controllers)
    : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        // Runs after the default ControllerFeatureProvider (registered last) and keeps only the selected controllers.
        foreach (var controller in feature.Controllers.Where(c => !controllers.Contains(c.AsType())).ToList())
        {
            feature.Controllers.Remove(controller);
        }
    }
}

internal static class TestApp
{
    public const string AdminPolicy = "Admin";

    /// <summary>
    ///     Starts a TestServer app with the given controllers, authentication, authorization, an exception handler and
    ///     <c>AddOctoRestDeprecation()</c> — the composition of an Octo service.
    /// </summary>
    public static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder>? configureServices = null,
        Action<WebApplication>? configureApp = null, params Type[] controllers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddOctoRestDeprecation();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o =>
            o.AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole("admin")));
        builder.Services.AddControllers().AddApplicationPart(typeof(TestApp).Assembly);
        configureServices?.Invoke(builder);
        // Last, so that it runs after the default provider even when configureServices adds MVC parts again.
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(m =>
            m.FeatureProviders.Add(new SelectedControllersFeatureProvider(controllers)));

        var app = builder.Build();
        app.UseExceptionHandler(errorApp => errorApp.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        configureApp?.Invoke(app);

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
