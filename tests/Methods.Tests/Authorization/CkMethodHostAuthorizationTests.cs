using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Authorization;

/// <summary>
///     The remote handler host and the in-process host enforce the declared authorization with the evaluator the gateway
///     uses (AB#7664, finding F-1 of AB#6915): a caller the definition does not allow never reaches the handler.
/// </summary>
public class CkMethodHostAuthorizationTests
{
    private const string Owner = "65f0c0ffee65f0c0ffee65f0";
    private const string OwnerUpper = "65F0C0FFEE65F0C0FFEE65F0";
    private const string Other = "65f0c0ffee65f0c0ffee65f1";
    private const string Scope = "octo_api";

    public sealed record Case(string Name, CkMethodAuthorizationDto? Declaration, CkMethodKindDto Kind,
        string? Target, CkMethodPrincipal? Principal, bool Allowed)
    {
        public override string ToString() => Name;
    }

    private static CkMethodPrincipal Caller(string? sub = Other, string[]? roles = null, string[]? scopes = null) =>
        new(sub, roles ?? [], scopes ?? [Scope]);

    private static CkMethodAuthorizationDto Declare(string[]? roles = null, string[]? scopes = null,
        bool allowSelf = false) =>
        new() { Roles = roles?.ToList(), Scopes = scopes?.ToList(), AllowSelf = allowSelf };

    public static IEnumerable<object[]> Cases() => All().Select(c => new object[] { c });

    private static Case[] All() =>
    [
        new("role matches", Declare(["AssetManagement"]), CkMethodKindDto.Instance, Owner, Caller(roles: ["assetmanagement"]), true),
        new("role missing", Declare(["AssetManagement"]), CkMethodKindDto.Instance, Owner, Caller(roles: ["Reporting"]), false),
        new("administrator is not admitted to a method that lists roles", Declare(["AssetManagement"]),
            CkMethodKindDto.Instance, Owner, Caller(roles: ["AdminPanelManagement"]), false),
        new("scope present", Declare(["R"], ["extra"]), CkMethodKindDto.Instance, Owner,
            Caller(roles: ["R"], scopes: [Scope, "extra"]), true),
        new("declared scope missing", Declare(["R"], ["extra"]), CkMethodKindDto.Instance, Owner,
            Caller(roles: ["R"]), false),
        new("api scope missing", Declare(["R"]), CkMethodKindDto.Instance, Owner, Caller(roles: ["R"], scopes: []), false),
        new("allowSelf with the caller's own id", Declare(allowSelf: true), CkMethodKindDto.Instance, Owner,
            Caller(Owner), true),
        new("allowSelf, own id in another letter case", Declare(allowSelf: true), CkMethodKindDto.Instance, OwnerUpper,
            Caller(Owner), true),
        new("allowSelf with another id", Declare(allowSelf: true), CkMethodKindDto.Instance, Other, Caller(Owner), false),
        new("allowSelf with another id, upper case", Declare(allowSelf: true), CkMethodKindDto.Instance,
            Other.ToUpperInvariant(), Caller(Owner), false),
        new("allowSelf on a static method", Declare(allowSelf: true), CkMethodKindDto.Static, Owner, Caller(Owner), false),
        new("no authorization, called by its owner (default-deny)", null, CkMethodKindDto.Instance, Owner,
            Caller(Owner), false),
        new("no authorization, administrator", null, CkMethodKindDto.Instance, Owner,
            Caller(roles: ["AdminPanelManagement"]), true),
        new("no caller", Declare(["R"]), CkMethodKindDto.Instance, Owner, null, false)
    ];

    private static CkMethodDto Definition(Case c)
    {
        var definition = TestData.Definition();
        definition.Authorization = c.Declaration;
        definition.Kind = c.Kind;
        return definition;
    }

    private static CkMethodAuthorizationEvaluator NewEvaluator() =>
        new(Options.Create(new CkMethodAuthorizationOptions()));

    private static void AssertSameError(CkMethodError expected, CkMethodError? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Code, actual.Code);
        Assert.Equal(expected.Parameters.OrderBy(p => p.Key), actual.Parameters.OrderBy(p => p.Key));
    }

    private static void AssertRefusedLikeTheGateway(CkMethodResponse response)
    {
        Assert.False(response.Success);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, response.Error!.Code);
        AssertSameError(CkMethodAuthorizationErrors.Forbidden(TestData.MethodId), response.Error);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RemoteHost_AppliesTheDeclaredAuthorization(Case c)
    {
        var handler = new ChangePasswordHandler();
        var host = c.Principal == null
            ? HostFixture.With(handler, Definition(c), withoutValidator: true)
            : HostFixture.With(handler, Definition(c), principal: c.Principal);

        var response = await host.Invoker.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { TargetRtId = c.Target }, TestContext.Current.CancellationToken);

        if (c.Allowed)
        {
            Assert.True(response.Success);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            AssertRefusedLikeTheGateway(response);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InProcessHost_AppliesTheDeclaredAuthorization(Case c)
    {
        var handler = new ChangePasswordHandler();
        var host = InProcessFixture.With(handler, Definition(c));
        var request = TestData.Request(host.InSeconds(15)) with { TargetRtId = c.Target };

        CkMethodResponse response;
        if (c.Principal == null)
        {
            // an unauthenticated caller: nothing is established, so the ambient default of the fixture does not apply
            var services = new ServiceCollection();
            services.AddScoped(_ => handler);
            services.AddSingleton<ICkMethodPrincipalProvider>(new NoPrincipalProvider());
            host = InProcessFixture.Build<ChangePasswordHandler>(services, Definition(c));
            response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
        }
        else
        {
            using (CkMethodPrincipalScope.Begin(c.Principal))
            {
                response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);
            }
        }

        if (c.Allowed)
        {
            Assert.True(response.Success);
            Assert.Equal(1, handler.Calls);
        }
        else
        {
            AssertRefusedLikeTheGateway(response);
            Assert.Equal(0, handler.Calls);
        }
    }

    /// <summary>One rule: the decision of the host equals the decision of the evaluator the gateway calls.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HostAndGateway_DecideIdentically(Case c)
    {
        var gateway = NewEvaluator().Evaluate(c.Declaration, c.Kind, c.Target, c.Principal);
        var handler = new ChangePasswordHandler();
        var host = c.Principal == null
            ? HostFixture.With(handler, Definition(c), withoutValidator: true)
            : HostFixture.With(handler, Definition(c), principal: c.Principal);

        var response = await host.Invoker.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { TargetRtId = c.Target }, TestContext.Current.CancellationToken);

        Assert.Equal(gateway.Allowed, response.Success);
        Assert.Equal(c.Allowed, gateway.Allowed);
    }

    [Fact]
    public async Task RemoteHost_RegistrationWithoutDefinition_IsRefused()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.Definition());
        host.Registrations.Handlers[TestData.MethodId].Definition = null;

        var response = await host.Invoker.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        AssertRefusedLikeTheGateway(response);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InProcessHost_RegistrationWithoutDefinition_IsRefused()
    {
        var handler = new ChangePasswordHandler();
        var host = InProcessFixture.With(handler, TestData.Definition());
        host.Registrations.Handlers[TestData.MethodId].Definition = null;

        CkMethodResponse response;
        using (CkMethodPrincipalScope.Begin(HostFixture.Administrator))
        {
            response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
                TestContext.Current.CancellationToken);
        }

        AssertRefusedLikeTheGateway(response);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Refusal_IsIdenticalForAnExistingAndAMissingTarget_AndNamesNeitherTokenNorTarget()
    {
        var handler = new ChangePasswordHandler();
        var definition = TestData.DefinitionWithRoles("AssetManagement");
        var host = HostFixture.With(handler, definition, principal: Caller(roles: ["Reporting"]));

        var existing = await host.Invoker.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { TargetRtId = Owner }, TestContext.Current.CancellationToken);
        var missing = await host.Invoker.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { TargetRtId = Other }, TestContext.Current.CancellationToken);

        AssertSameError(existing.Error!, missing.Error);
        Assert.Equal(CkMethodAuthorizationErrors.ReasonNotAuthorized,
            existing.Error!.Parameters[CkMethodErrorParameters.Reason]);
        var log = host.AllLogText();
        Assert.DoesNotContain(Owner, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Other, log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteHost_RefusesBeforeTheParametersAreInterpreted()
    {
        var handler = new ChangePasswordHandler();
        var host = HostFixture.With(handler, TestData.DefinitionWithRoles("AssetManagement"),
            principal: Caller(roles: ["Reporting"]));

        var response = await host.Invoker.InvokeAsync(
            TestData.Request(host.InSeconds(15)) with { ParametersJson = "{ not json" },
            TestContext.Current.CancellationToken);

        // a caller who is not allowed learns nothing about the validity of the parameters
        AssertRefusedLikeTheGateway(response);
    }
}
