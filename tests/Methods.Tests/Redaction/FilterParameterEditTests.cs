using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.Handlers;
using Meshmakers.Octo.Services.Methods.Tests.InProcess;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Redaction;

/// <summary>
///     AB#7016: a filter that edits <c>ParametersJson</c> starting from the redacted view must never make the handler
///     receive the placeholder as a real value. The pipeline merges per member.
/// </summary>
public class FilterParameterEditTests
{
    private const string Mask = CkMethodRedactor.Mask;

    private static async Task<(ChangePasswordHandler Handler, CkMethodResponse Response)> RunAsync(
        Action<JsonObject> edit, string? parametersJson = null)
    {
        var handler = new ChangePasswordHandler();
        var filter = new EditingFilter(edit);
        var host = InProcessFixture.With(handler, TestData.Definition(), TestData.MethodId, filter);
        var response = await host.Transport.InvokeAsync(
            TestData.Request(host.InSeconds(15), parametersJson: parametersJson),
            TestContext.Current.CancellationToken);
        return (handler, response);
    }

    [Fact]
    public async Task EditingANonSensitiveMember_KeepsTheRawSensitiveValues()
    {
        var (handler, response) = await RunAsync(p => p["hint"] = "normalized");

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(TestData.Secret, handler.LastContext!.Parameters.NewPassword);
        Assert.Equal(TestData.OldSecret, handler.LastContext.Parameters.CurrentPassword);
        Assert.Equal("normalized", handler.LastContext.Parameters.Hint);
    }

    [Fact]
    public async Task ReplacingASensitiveMember_IsHonoured_TheOtherOneStaysRaw()
    {
        var (handler, response) = await RunAsync(p => p["newPassword"] = "replaced");

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal("replaced", handler.LastContext!.Parameters.NewPassword);
        Assert.Equal(TestData.OldSecret, handler.LastContext.Parameters.CurrentPassword);
    }

    [Fact]
    public async Task EditsWithEscapableSecrets_RestoreTheExactRawValue()
    {
        const string secret = "pa\"ss\\w+rd&<ä>\t";
        var json = JsonSerializer.Serialize(new { currentPassword = "old", newPassword = secret });

        var (handler, response) = await RunAsync(p => p["hint"] = "x", json);

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(secret, handler.LastContext!.Parameters.NewPassword);
    }

    [Fact]
    public async Task ARemovedSensitiveMember_IsAnExplicitEdit_AndStaysRemoved()
    {
        var (handler, response) = await RunAsync(p => p.Remove("currentPassword"));

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task APlaceholderForAMemberTheRequestDoesNotCarry_IsRefused_AndTheHandlerIsNeverCalled()
    {
        var json = JsonSerializer.Serialize(new { currentPassword = "old" });

        var (handler, response) = await RunAsync(p => p["newPassword"] = Mask, json);

        Assert.Equal(CkMethodErrorCodes.MethodValidationFailed, response.Error!.Code);
        Assert.Equal("newPassword", response.Error.Parameters[CkMethodErrorParameters.Field]);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain(TestData.Secret, JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task APlaceholderInANonSensitiveMember_IsNotTouched()
    {
        var (handler, response) = await RunAsync(p => p["hint"] = Mask);

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(Mask, handler.LastContext!.Parameters.Hint);
        Assert.Equal(TestData.Secret, handler.LastContext.Parameters.NewPassword);
    }

    [Fact]
    public void TryRestore_MatchesNamesCaseInsensitively_AndRestoresNestedValuesWhole()
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5),
            parametersJson: "{\"Secret\":{\"a\":[\"s2\",3]},\"open\":1}");
        var sensitive = new HashSet<string> { "secret" };
        var view = CkMethodRequestRedaction.CreateView(raw, sensitive);
        var passed = view with { ParametersJson = "{\"secret\":\"***\",\"open\":2,\"added\":true}" };

        Assert.True(CkMethodRequestRedaction.TryRestore(raw, view, passed, sensitive, out var restored, out _));

        Assert.Equal("{\"secret\":{\"a\":[\"s2\",3]},\"open\":2,\"added\":true}", restored.ParametersJson);
        Assert.Equal(raw.CallerAccessToken, restored.CallerAccessToken);
    }

    [Fact]
    public void TryRestore_UnchangedParameters_GiveTheRawJsonByteForByte()
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5));
        var view = CkMethodRequestRedaction.CreateView(raw, new HashSet<string> { "newPassword" });

        Assert.True(CkMethodRequestRedaction.TryRestore(raw, view, view, new HashSet<string> { "newPassword" },
            out var restored, out _));

        Assert.Equal(raw.ParametersJson, restored.ParametersJson);
    }

    [Fact]
    public void TryRestore_UnknownMethod_TreatsEveryMemberAsSensitive()
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5), parametersJson: "{\"a\":\"one\",\"b\":\"two\"}");
        var view = CkMethodRequestRedaction.CreateView(raw, sensitive: null);
        var passed = view with { ParametersJson = "{\"a\":\"***\",\"b\":\"changed\",\"c\":\"***\"}" };

        Assert.False(CkMethodRequestRedaction.TryRestore(raw, view, passed, null, out _, out var member));
        Assert.Equal("c", member);

        passed = view with { ParametersJson = "{\"a\":\"***\",\"b\":\"changed\"}" };
        Assert.True(CkMethodRequestRedaction.TryRestore(raw, view, passed, null, out var restored, out _));
        Assert.Equal("{\"a\":\"one\",\"b\":\"changed\"}", restored.ParametersJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[\"***\"]")]
    public void TryRestore_APayloadThatIsNoObject_IsPassedOnForTheHandlerSideToReject(string payload)
    {
        var raw = TestData.Request(DateTime.UtcNow.AddSeconds(5));
        var view = CkMethodRequestRedaction.CreateView(raw, new HashSet<string> { "newPassword" });

        Assert.True(CkMethodRequestRedaction.TryRestore(raw, view, view with { ParametersJson = payload },
            new HashSet<string> { "newPassword" }, out var restored, out _));

        Assert.Equal(payload, restored.ParametersJson);
    }

    private sealed class EditingFilter(Action<JsonObject> edit) : ICkMethodInvocationFilter
    {
        public int Order => CkMethodInvocationFilterOrder.Idempotency;

        public Task<CkMethodResponse> InvokeAsync(CkMethodInvocationContext context, CkMethodInvocationDelegate next)
        {
            var node = JsonNode.Parse(context.Request.ParametersJson)!.AsObject();
            edit(node);
            return next(context.Request with { ParametersJson = node.ToJsonString() });
        }
    }
}
