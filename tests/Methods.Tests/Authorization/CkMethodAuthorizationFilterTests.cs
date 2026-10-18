using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

public class CkMethodAuthorizationFilterTests
{
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string OwnerUpper = "65F0C0FFEE65F0C0FFEE65F0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";

    private static CkMethodPrincipal Caller(string? sub = Other, string[]? roles = null, string[]? scopes = null) =>
        new(sub, roles ?? [], scopes ?? [CkMethodAuthorizationOptions.ApiScope]);

    private static CkMethodDto Definition(CkMethodAuthorizationDto? authorization, bool isStatic = false)
    {
        var definition = TestData.Definition();
        definition.Authorization = authorization;
        definition.Kind = isStatic ? CkMethodKindDto.Static : CkMethodKindDto.Instance;
        return definition;
    }

    private static CkMethodAuthorizationFilter NewFilter(CkMethodDto? definition) =>
        new(new CkMethodPrincipalScope(),
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            new FixedSource(definition), NullLogger<CkMethodAuthorizationFilter>.Instance);

    /// <summary>The host of the in-process transport with the audit (100) and a stub idempotency (300) filter around it.</summary>
    private sealed class Rig
    {
        public Rig(CkMethodDto? definition, params ICkMethodInvocationFilter[] extraFilters)
        {
            Handler = new ChangePasswordHandler();
            Trace = [];
            Audit = new TraceFilter(CkMethodInvocationFilterOrder.Audit, Trace, "audit");
            Idempotency = new TraceFilter(CkMethodInvocationFilterOrder.Idempotency, Trace, "idempotency");
            // registered in "any order": inner first, outer last
            Host = InProcessFixture.With(Handler, TestData.Definition(), TestData.MethodId,
                [Idempotency, NewFilter(definition), Audit, .. extraFilters]);
        }

        public ChangePasswordHandler Handler { get; }

        public List<string> Trace { get; }

        public TraceFilter Audit { get; }

        public TraceFilter Idempotency { get; }

        public InProcessFixture Host { get; }

        public async Task<CkMethodResponse> CallAsync(CkMethodPrincipal? principal, string? target = Owner)
        {
            var request = TestData.Request(Host.InSeconds(15)) with { TargetRtId = target };
            using (principal == null ? null : CkMethodPrincipalScope.Begin(principal))
            {
                return await Host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task Role_CallerWithTheRole_PassesAndTheHandlerRuns()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] }));

        var response = await rig.CallAsync(Caller(roles: ["assetmanagement"]));

        Assert.True(response.Success);
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public async Task Role_CallerWithoutTheRole_IsForbidden_AndTheHandlerIsNotCalled()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] }));

        var response = await rig.CallAsync(Caller(roles: ["Other"]));

        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task AllowSelf_DifferentLetterCase_Passes_AnotherCallerIsRefused()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { AllowSelf = true }));

        Assert.True((await rig.CallAsync(Caller(OwnerUpper), Owner)).Success);
        Assert.True((await rig.CallAsync(Caller(Owner), OwnerUpper)).Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, (await rig.CallAsync(Caller(Other), Owner)).Error!.Code);
        Assert.Equal(2, rig.Handler.Calls);
    }

    [Fact]
    public async Task Scopes_MissingApiScopeRefusesEvenWhenNoScopeIsListed()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["R"] }));

        Assert.False((await rig.CallAsync(Caller(roles: ["R"], scopes: ["other"]))).Success);
        Assert.False((await rig.CallAsync(Caller(roles: ["R"], scopes: []))).Success);
        Assert.True((await rig.CallAsync(Caller(roles: ["R"]))).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultDeny_OnlyAdministratorsPass_TheOwnerIsRefused(bool emptyBlock)
    {
        var rig = new Rig(Definition(emptyBlock ? new CkMethodAuthorizationDto { Roles = [] } : null));

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, (await rig.CallAsync(Caller(Owner), Owner)).Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);
        Assert.True((await rig.CallAsync(Caller(Other, ["AdminPanelManagement"]), Owner)).Success);
    }

    [Fact]
    public async Task NoPrincipal_IsForbidden()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["R"] }));

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, (await rig.CallAsync(principal: null)).Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task ForgedCallerOfTheRequest_HasNoEffect()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["AssetManagement"] }));
        var request = TestData.Request(rig.Host.InSeconds(15)) with
        {
            TargetRtId = Owner,
            Caller = new CkMethodCaller { SubjectId = Owner, Roles = ["AssetManagement", "AdminPanelManagement"] }
        };

        var response = await rig.Host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task UnknownDefinition_IsForbidden_NotAnException()
    {
        var rig = new Rig(definition: null);

        var response = await rig.CallAsync(Caller(roles: ["AdminPanelManagement"]));

        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task Refusal_IsIdenticalWhetherTheTargetExistsOrNot()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["AssetManagement"], AllowSelf = true }));
        var caller = Caller(roles: ["Other"]);

        var existing = await rig.CallAsync(caller, Owner);
        var missing = await rig.CallAsync(caller, "000000000000000000000000");
        var garbage = await rig.CallAsync(caller, "no-such-entity");

        static string Shape(CkMethodResponse r) => System.Text.Json.JsonSerializer.Serialize(
            r with { InvocationId = Guid.Empty });
        Assert.Equal(Shape(existing), Shape(missing));
        Assert.Equal(Shape(existing), Shape(garbage));
        Assert.Equal(CkMethodAuthorizationErrors.ReasonNotAuthorized,
            existing.Error!.Parameters[CkMethodErrorParameters.Reason]);
        Assert.Equal(TestData.MethodId, existing.Error.Parameters[CkMethodErrorParameters.MethodId]);
    }

    [Fact]
    public async Task FilterOrder_IsAuditAuthorizationIdempotency_AndARefusedCallReachesOnlyTheAudit()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["R"] }));

        var refused = await rig.CallAsync(Caller(roles: ["Other"]));
        Assert.False(refused.Success);
        Assert.Equal(["audit>", "audit<"], rig.Trace); // seen by the audit, never by the idempotency filter
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, rig.Audit.LastResponse!.Error!.Code);
        Assert.Equal(0, rig.Handler.Calls);

        rig.Trace.Clear();
        var allowed = await rig.CallAsync(Caller(roles: ["R"]));
        Assert.True(allowed.Success);
        Assert.Equal(["audit>", "idempotency>", "idempotency<", "audit<"], rig.Trace);
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public void Filter_SitsAtTheAuthorizationBand()
    {
        Assert.Equal(CkMethodInvocationFilterOrder.Authorization, NewFilter(null).Order);
        Assert.Equal(200, NewFilter(null).Order);
    }

    [Fact]
    public async Task RefusalLog_NamesTheRule_ButNotTheTokenParametersOrTarget()
    {
        var rig = new Rig(Definition(new CkMethodAuthorizationDto { Roles = ["R"] }));
        // the fixture's logger collects the pipeline logs; the filter logs through its own logger here
        var logs = new Microsoft.Extensions.Logging.Testing.FakeLogCollector();
        var logger = new Microsoft.Extensions.Logging.Testing.FakeLogger<CkMethodAuthorizationFilter>(logs);
        var filter = new CkMethodAuthorizationFilter(new CkMethodPrincipalScope(),
            new CkMethodAuthorizationEvaluator(Options.Create(new CkMethodAuthorizationOptions())),
            new FixedSource(Definition(new CkMethodAuthorizationDto { Roles = ["R"] })), logger);
        var request = TestData.Request(rig.Host.InSeconds(15)) with { TargetRtId = Owner };

        using (CkMethodPrincipalScope.Begin(Caller(roles: ["Other"])))
        {
            await filter.InvokeAsync(new CkMethodInvocationContext(CkMethodTransportNames.Bus, request,
                TestContext.Current.CancellationToken), _ => throw new InvalidOperationException("must not run"));
        }

        var text = string.Join("\n", logs.GetSnapshot().Select(r => r.Message));
        Assert.Contains("NotAuthorized", text);
        Assert.DoesNotContain(TestData.Token, text);
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(Owner, text);
    }

    // ---- registration ----------------------------------------------------------------------------------------

    [Fact]
    public void AddCkMethodAuthorization_RegistersTheFilterOnce_AndTheSharedEvaluator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(A.Fake<ICkCacheService>());

        services.AddCkMethodAuthorization(o => o.AdministratorRoles = ["Root"]);
        services.AddCkMethodAuthorization();
        using var provider = services.BuildServiceProvider();

        var filters = provider.GetServices<ICkMethodInvocationFilter>().ToList();
        Assert.Single(filters);
        Assert.IsType<CkMethodAuthorizationFilter>(filters[0]);
        Assert.Equal(CkMethodInvocationFilterOrder.Authorization, filters[0].Order);
        Assert.IsType<CkMethodAuthorizationEvaluator>(provider.GetRequiredService<ICkMethodAuthorizationEvaluator>());
        Assert.Equal(["Root"], provider.GetRequiredService<IOptions<CkMethodAuthorizationOptions>>().Value.AdministratorRoles);
        Assert.NotNull(provider.GetRequiredService<ICkMethodInvocationPipeline>());
    }

    // ---- definition source -----------------------------------------------------------------------------------

    private const string Tenant = "meshtest";

    private static ICkCacheService CacheWith(string concreteType, string declaring, CkMethodDto definition)
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        var graph = new CkMethodGraph(new CkId<CkTypeId>(declaring), definition);
        var type = new CkTypeGraph(new CkId<CkTypeId>(concreteType), false, false, false, [], null, null, [], [],
            new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), [], new CkGraphDirectedAssociations([]), "", false,
            allMethods: new Dictionary<string, CkMethodGraph> { [definition.MethodId] = graph });
        var rtId = new RtCkId<CkTypeId>(concreteType.Replace("-1.0.0", ""));
        A.CallTo(() => cache.TryGetRtCkType(Tenant, rtId, out type)).Returns(true);
        return cache;
    }

    [Fact]
    public void CacheSource_FindsAnInheritedMethodThroughTheConcreteTargetType()
    {
        var definition = new CkMethodDto
        {
            MethodId = "ChangePassword-1",
            Authorization = new CkMethodAuthorizationDto { Roles = ["R"] }
        };
        var cache = CacheWith("Sample.Accounts-1.0.0/AdminAccount", "Sample.Accounts-1.0.0/Account", definition);
        var source = new CkCacheMethodAuthorizationDefinitionSource(cache,
            NullLogger<CkCacheMethodAuthorizationDefinitionSource>.Instance);

        var found = source.Find(Tenant, "Sample.Accounts/AdminAccount", "Sample.Accounts/Account.ChangePassword-1");

        Assert.Same(definition, found);
    }

    [Fact]
    public void CacheSource_UnknownTenantTypeMethodOrMalformedIds_AreNull_NeverThrow()
    {
        var cache = A.Fake<ICkCacheService>();
        var source = new CkCacheMethodAuthorizationDefinitionSource(cache,
            NullLogger<CkCacheMethodAuthorizationDefinitionSource>.Instance);

        Assert.Null(source.Find(Tenant, "Sample.Accounts/Account", "Sample.Accounts/Account.ChangePassword-1")); // not loaded
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        Assert.Null(source.Find(Tenant, "Sample.Accounts/Account", "Sample.Accounts/Account.ChangePassword-1")); // unknown type
        Assert.Null(source.Find(Tenant, "Sample.Accounts/Account", "not a method id"));
        Assert.Null(source.Find(Tenant, "###", "Sample.Accounts/Account.ChangePassword-1"));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private sealed class FixedSource(CkMethodDto? definition) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => definition;
    }

    internal sealed class TraceFilter(int order, List<string> trace, string name) : ICkMethodInvocationFilter
    {
        public int Order => order;

        public CkMethodResponse? LastResponse { get; private set; }

        public async Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            trace.Add(name + ">");
            var response = await next(context.Request);
            LastResponse = response;
            trace.Add(name + "<");
            return response;
        }
    }
}
