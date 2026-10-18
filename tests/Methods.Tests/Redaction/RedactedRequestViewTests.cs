using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Redaction;

/// <summary>AB#6914: filters see a redacted view of the request, only the transport gets the raw values.</summary>
public class RedactedRequestViewTests
{
    private const string Mask = "***";

    private static string Param(CkMethodRequest request, string name) =>
        JsonDocument.Parse(request.ParametersJson).RootElement.GetProperty(name).GetString()!;

    [Fact]
    public async Task OutermostFilter_SeesPlaceholdersAndNoToken_ButTheHandlerGetsTheRawValues()
    {
        var seen = new List<CkMethodRequest>();
        var handler = new ChangePasswordHandler();
        var audit = new SeeingFilter(CkMethodInvocationFilterOrder.Audit, seen);
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, audit);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.True(response.Success, response.Error?.Code);
        var view = Assert.Single(seen);
        Assert.Equal(Mask, Param(view, "newPassword"));
        Assert.Equal(Mask, Param(view, "currentPassword"));
        Assert.Null(view.CallerAccessToken);
        Assert.DoesNotContain(TestData.Secret, view.ParametersJson);
        Assert.DoesNotContain(TestData.OldSecret, view.ParametersJson);
        // the raw values reached the transport and the handler
        Assert.Equal(TestData.Secret, handler.LastContext!.Parameters.NewPassword);
        Assert.Equal(TestData.OldSecret, handler.LastContext.Parameters.CurrentPassword);
        Assert.Equal(TestData.Token, handler.LastContext.CallerAccessToken);
    }

    [Fact]
    public async Task NonSensitiveParameters_AndAllOtherMembers_StayVisible()
    {
        var seen = new List<CkMethodRequest>();
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId,
            new SeeingFilter(CkMethodInvocationFilterOrder.Audit, seen));
        var request = TestData.Request(host.InSeconds(15),
            parametersJson: $"{{\"currentPassword\":\"{TestData.OldSecret}\",\"newPassword\":\"{TestData.Secret}\",\"hint\":\"my dog\"}}");

        await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        var view = Assert.Single(seen);
        Assert.Equal("my dog", Param(view, "hint"));
        Assert.Equal(request.MethodId, view.MethodId);
        Assert.Equal(request.TargetRtId, view.TargetRtId);
        Assert.Equal(request.InvocationId, view.InvocationId);
        Assert.Equal(request.Caller, view.Caller);
        Assert.Equal(request.DeadlineUtc, view.DeadlineUtc);
    }

    [Fact]
    public void Placeholder_ReplacesWholeValues_CaseInsensitive_AndNestedStructures()
    {
        var definition = new CkMethodDto
        {
            MethodId = "M-1",
            Parameters =
            [
                new CkMethodParameterDto { Name = "Secret", ValueType = AttributeValueTypesDto.String, Sensitive = true },
                new CkMethodParameterDto { Name = "blob", ValueType = AttributeValueTypesDto.String, Sensitive = true },
                new CkMethodParameterDto { Name = "number", ValueType = AttributeValueTypesDto.Int, Sensitive = true },
                new CkMethodParameterDto { Name = "open", ValueType = AttributeValueTypesDto.String }
            ]
        };
        var sensitive = new DefinitionSensitivity(definition).GetSensitiveParameters("t", "T/X", "T/X.M-1")!;
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5),
            parametersJson: "{\"secret\":\"s1\",\"blob\":{\"a\":[\"s2\",3]},\"number\":424242,\"open\":\"visible\"}");

        var view = CkMethodRequestRedaction.CreateView(raw, sensitive);

        Assert.Equal($"{{\"secret\":\"{Mask}\",\"blob\":\"{Mask}\",\"number\":\"{Mask}\",\"open\":\"visible\"}}",
            view.ParametersJson);
    }

    [Theory]
    [InlineData("{\"newPassword\":\"CANARY-SECRET-123")]
    [InlineData("not json CANARY-SECRET-123")]
    [InlineData("[\"CANARY-SECRET-123\"]")]
    [InlineData("\"CANARY-SECRET-123\"")]
    [InlineData("")]
    public void UnparsableOrNonObjectPayload_IsNeverShown(string payload)
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5), parametersJson: payload);

        var view = CkMethodRequestRedaction.CreateView(raw, new HashSet<string> { "newPassword" });

        Assert.Equal("{}", view.ParametersJson);
        Assert.Null(view.CallerAccessToken);
    }

    [Fact]
    public void NamesMatchCaseInsensitively_EvenForASetWithTheDefaultComparer()
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5),
            parametersJson: "{\"NewPassword\":\"CANARY-SECRET-123\",\"hint\":\"x\"}");

        var view = CkMethodRequestRedaction.CreateView(raw, new HashSet<string> { "newPassword" });

        Assert.Equal($"{{\"NewPassword\":\"{Mask}\",\"hint\":\"x\"}}", view.ParametersJson);
    }

    [Fact]
    public void UnknownMethod_RedactsEveryParameterValue_FailClosed()
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5),
            parametersJson: "{\"a\":\"CANARY-SECRET-123\",\"b\":1}");

        var view = CkMethodRequestRedaction.CreateView(raw, sensitive: null);

        Assert.Equal($"{{\"a\":\"{Mask}\",\"b\":\"{Mask}\"}}", view.ParametersJson);
    }

    [Fact]
    public async Task AFilterThatChangesTheRequest_KeepsItsChange_AndTheRawValuesStillReachTheHandler()
    {
        var handler = new ChangePasswordHandler();
        var rewrite = new SeeingFilter(CkMethodInvocationFilterOrder.Idempotency, [])
        {
            Rewrite = r => r with { IdempotencyKey = "key-1" }
        };
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, rewrite);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal("key-1", handler.LastContext!.IdempotencyKey);
        Assert.Equal(TestData.Secret, handler.LastContext.Parameters.NewPassword);
        Assert.Equal(TestData.Token, handler.LastContext.CallerAccessToken);
    }

    [Fact]
    public async Task AFilterThatReplacesTheParameters_IsHonoured()
    {
        var handler = new ChangePasswordHandler();
        var rewrite = new SeeingFilter(CkMethodInvocationFilterOrder.Idempotency, [])
        {
            Rewrite = r => r with
            {
                ParametersJson = "{\"currentPassword\":\"a\",\"newPassword\":\"replaced\"}"
            }
        };
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, rewrite);

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal("replaced", handler.LastContext!.Parameters.NewPassword);
    }

    [Fact]
    public async Task InnerFilters_SeeTheViewToo()
    {
        var outer = new List<CkMethodRequest>();
        var inner = new List<CkMethodRequest>();
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId,
            new SeeingFilter(CkMethodInvocationFilterOrder.Audit, outer),
            new SeeingFilter(CkMethodInvocationFilterOrder.Authorization, inner),
            new SeeingFilter(CkMethodInvocationFilterOrder.Idempotency, []));

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(Mask, Param(Assert.Single(outer), "newPassword"));
        Assert.Equal(Mask, Param(Assert.Single(inner), "newPassword"));
    }

    [Fact]
    public async Task ContextToString_AndItsJson_AreSafe()
    {
        CkMethodInvocationContext? captured = null;
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId,
            new SeeingFilter(CkMethodInvocationFilterOrder.Audit, [], c => captured = c));

        await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)), TestContext.Current.CancellationToken);

        var text = captured!.ToString();
        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, text);
        // even serialised member by member, the view holds neither value
        var json = JsonSerializer.Serialize(captured.Request);
        Assert.DoesNotContain(TestData.Secret, json);
        Assert.DoesNotContain(TestData.OldSecret, json);
        Assert.DoesNotContain(TestData.Token, json);
    }

    [Fact]
    public async Task ThrowingFilter_WithTheSensitiveValueInItsMessage_IsLoggedMasked()
    {
        var broken = new SeeingFilter(CkMethodInvocationFilterOrder.Audit, [])
        {
            Throw = () => new InvalidOperationException(
                $"audit store rejected {TestData.Secret} and {{\"newPassword\":\"{TestData.Secret}\"}} token {TestData.Token}")
        };
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId, broken);

        var response = await host.Transport.InvokeAsync(TestData.Request(host.InSeconds(15)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, response.Error!.Code);
        var logs = host.AllLogText();
        Assert.DoesNotContain(TestData.Secret, logs);
        Assert.DoesNotContain(TestData.OldSecret, logs);
        Assert.DoesNotContain(TestData.Token, logs);
        Assert.Contains(CkMethodRedactor.Mask, logs);
    }

    [Fact]
    public async Task MalformedJsonForASensitiveParameter_NamesTheParameter_AndNeverEchoesTheValue()
    {
        var host = InProcessFixture.With(new ChangePasswordHandler(), TestData.Definition(), TestData.MethodId);
        var request = TestData.Request(host.InSeconds(15),
            parametersJson: $"{{\"currentPassword\":\"a\",\"newPassword\":{{\"x\":\"{TestData.Secret}\"}}}}");

        var response = await host.Transport.InvokeAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Contains("newPassword", response.Error.Parameters[CkMethodErrorParameters.Field],
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestData.Secret, JsonSerializer.Serialize(response));
        Assert.DoesNotContain(TestData.Secret, host.AllLogText());

        var truncated = await host.Transport.InvokeAsync(
            TestData.Request(host.InSeconds(15), parametersJson: $"{{\"newPassword\":\"{TestData.Secret}"),
            TestContext.Current.CancellationToken);
        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, truncated.Error!.Code);
        Assert.DoesNotContain(TestData.Secret, JsonSerializer.Serialize(truncated));
        Assert.DoesNotContain(TestData.Secret, host.AllLogText());
    }

    [Fact]
    public void Resolver_WithoutASource_OrWithAFailingOne_IsFailClosed()
    {
        Assert.Null(new CkMethodSensitivityResolver(null).GetSensitiveParameters("t", "T/X", "T/X.M-1"));
        Assert.Null(new CkMethodSensitivityResolver(new ThrowingSource()).GetSensitiveParameters("t", "T/X", "T/X.M-1"));
        Assert.Null(new CkMethodSensitivityResolver(new FixedSource(null)).GetSensitiveParameters("t", "T/X", "T/X.M-1"));
    }

    [Fact]
    public void Resolver_ReturnsTheSensitiveNamesOfTheDefinition()
    {
        var names = new CkMethodSensitivityResolver(new FixedSource(TestData.Definition()))
            .GetSensitiveParameters("t", "T/X", "T/X.M-1")!;

        Assert.Equivalent(new[] { "currentPassword", "newPassword" }, names);
        Assert.Contains("NEWPASSWORD", names);
    }

    [Fact]
    public void Redactor_ForAnUnknownMethod_MasksEveryValue_AndAnUnparsablePayloadAsAWhole()
    {
        var all = CkMethodRedactor.For((IReadOnlySet<string>?)null,
            TestData.Request(DateTime.UtcNow.AddSeconds(5), parametersJson: "{\"a\":\"CANARY-ONE\",\"b\":\"CANARY-TWO\"}"));
        Assert.Equal("x *** y *** z", all.Redact("x CANARY-ONE y CANARY-TWO z"));

        var broken = CkMethodRedactor.For(new HashSet<string> { "newPassword" },
            TestData.Request(DateTime.UtcNow.AddSeconds(5), parametersJson: "{\"newPassword\":\"CANARY-THREE"));
        Assert.DoesNotContain("CANARY-THREE", broken.Redact("bad json: {\"newPassword\":\"CANARY-THREE"));
    }

    private sealed class FixedSource(CkMethodDto? definition) : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) => definition;
    }

    private sealed class ThrowingSource : ICkMethodAuthorizationDefinitionSource
    {
        public CkMethodDto? Find(string tenantId, string ckTypeId, string qualifiedMethodId) =>
            throw new InvalidOperationException("cache fault");
    }

    private sealed class SeeingFilter(int order, List<CkMethodRequest> seen, Action<CkMethodInvocationContext>? capture = null)
        : ICkMethodInvocationFilter
    {
        public Func<CkMethodRequest, CkMethodRequest>? Rewrite { get; init; }

        public Func<Exception>? Throw { get; init; }

        public int Order => order;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            seen.Add(context.Request);
            capture?.Invoke(context);
            if (Throw != null)
            {
                throw Throw();
            }

            return next(Rewrite?.Invoke(context.Request) ?? context.Request);
        }
    }
}
