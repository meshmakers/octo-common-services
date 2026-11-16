using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static Meshmakers.Octo.Services.Methods.Tests.Authorization.ParentTenantAdministrationKit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     Parity of the parent-tenant administration rule: the same cases run against the REST rule (the real
///     <c>TenantAuthorizationMiddleware</c> in front of a jobs route marked <c>[AllowParentTenantAdministration]</c> with the
///     policy of the bot service's jobs actions: scope <c>octo_api</c> and role <c>TenantManagement</c>) and against the method
///     path (the gateway order: caller-tenant check with the closed method list, tenant generation of the token tenant, the
///     declared roles of <c>TenantRepository.Dump-1</c>), and must reach the same allow/deny outcome. Every mode enforces.
/// </summary>
public class ParentTenantAdministrationParityTests
{
    private const string Dump = CkParentTenantAdministrationMethods.TenantRepositoryDump;
    private const string TenantJobManagementPolicy = "TenantJobManagement";

    public sealed record Case(string Name, string TokenTenant, string CallTenant, bool User, string? ClientId,
        bool HasRole, string Generation, bool Expected)
    {
        public override string ToString() => Name;
    }

    private static readonly Case[] All =
    [
        new Case("own tenant", Parent, Parent, true, "studio", true, CurrentGeneration, true),
        new Case("parent on its child", Parent, Child, true, "studio", true, CurrentGeneration, true),
        new Case("child on its sibling", Child, Sibling, true, "studio", true, CurrentGeneration, false),
        new Case("parent on a tenant that is not its child", Parent, Unrelated, true, "studio", true, CurrentGeneration, false),
        new Case("parent on its grandchild", Parent, Grandchild, true, "studio", true, CurrentGeneration, false),
        new Case("child on its parent", Child, Parent, true, "studio", true, CurrentGeneration, false),
        new Case("parent on a deleted or unregistered child", Parent, "gone1", true, "studio", true, CurrentGeneration, false),
        new Case("system tenant on a grandchild", SystemTenant, Grandchild, true, "studio", true, CurrentGeneration, true),
        new Case("client credentials of the parent on its child", Parent, Child, false, "parent-client", true, CurrentGeneration, false),
        new Case("allow-listed client credentials on the child", Parent, Child, false, CrossClient, true, CurrentGeneration, true),
        new Case("parent on its child without the role", Parent, Child, true, "studio", false, CurrentGeneration, false),
        new Case("parent recreated after the token was issued", Parent, Child, true, "studio", true, OldGeneration, false),
        new Case("parent token carrying the child's generation", Parent, Child, true, "studio", true, ChildGeneration, false)
    ];

    public static TheoryData<string> Cases() => new(All.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RestAndMethod_ReachTheSameOutcome(string name)
    {
        var c = All.Single(x => x.Name == name);
        var rest = await RestAllowsAsync(c);
        var method = await MethodAllowsAsync(c);

        Assert.True(c.Expected == rest, $"REST, {c.Name}: expected {c.Expected}, got {rest}");
        Assert.True(c.Expected == method, $"method, {c.Name}: expected {c.Expected}, got {method}");
    }

    private static ClaimsPrincipal Principal(Case c)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", c.TokenTenant),
            new("scope", CkMethodAuthorizationOptions.ApiScope),
            new(TenantGenerationClaim.ClaimType, c.Generation)
        };
        if (c.User)
        {
            claims.Add(new Claim("sub", "0123456789abcdef01234567"));
        }

        if (c.ClientId is not null)
        {
            claims.Add(new Claim("client_id", c.ClientId));
        }

        if (c.HasRole)
        {
            claims.Add(new Claim("role", CkParentTenantAdministrationMethods.RequiredRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    // ---------------------------------------------------------------- method path (gateway order)

    private static async Task<bool> MethodAllowsAsync(Case c)
    {
        var options = EnforcingOptions();
        var principal = CkMethodPrincipal.FromClaimsPrincipal(Principal(c));

        var tenant = await CkMethodCallerTenantCheck.CheckAsync(principal, c.CallTenant, Dump, options, new Hierarchy(),
            NullLogger.Instance, "gateway");
        if (tenant.IsRefused)
        {
            return false;
        }

        var generation = await CkMethodCallerTenantRule.CheckGenerationAsync(GenerationCheck(options), options,
            principal.SubjectId, principal.TokenClientId ?? principal.ClientId, principal.TokenTenantId,
            principal.TokenTenantGeneration, c.CallTenant, CancellationToken.None);
        if (generation != TenantGenerationDecision.Allow)
        {
            return false;
        }

        // the declaration of TenantRepository.Dump-1: authorization.roles [TenantManagement], static
        var evaluator = new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions()));
        var decision = evaluator.Evaluate(
            new CkMethodAuthorizationDto { Roles = [CkParentTenantAdministrationMethods.RequiredRole] },
            CkMethodKindDto.Static, null, principal);
        return decision.Allowed;
    }

    // ---------------------------------------------------------------- REST path (real middleware)

    private static async Task<bool> RestAllowsAsync(Case c)
    {
        var options = EnforcingOptions();
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, CaseAuthenticationHandler>("Test", _ => { });
                    services.AddAuthorization(o => o.AddPolicy(TenantJobManagementPolicy, p => p
                        .RequireClaim("scope", CkMethodAuthorizationOptions.ApiScope)
                        .RequireRole(CkParentTenantAdministrationMethods.RequiredRole)));
                    services.AddSingleton(Options.Create(options));
                    services.AddSingleton<ITenantHierarchyReader>(new Hierarchy());
                    services.AddSingleton(GenerationCheck(options));
                    services.AddSingleton(Principal(c));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseOctoTenantAuthorization();
                    app.UseEndpoints(endpoints => endpoints
                        .MapPost("/{tenantId}/system/v1/jobs/dump", () => Results.Ok())
                        .RequireAuthorization(TenantJobManagementPolicy)
                        .WithMetadata(new AllowParentTenantAdministrationAttribute()));
                }))
            .StartAsync(TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{c.CallTenant}/system/v1/jobs/dump");
        request.Headers.Add("Authorization", "Bearer test-token");
        var response = await host.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private sealed class CaseAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ClaimsPrincipal principal) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
