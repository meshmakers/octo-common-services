using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Typed;

public class CkMethodTypedApiTests
{
    [Fact]
    public void Failure_AcceptsDeclaredAndPlatformCodes_RejectsOthers()
    {
        Assert.Equal("PASSWORD_TOO_SHORT", new CkMethodFailure("PASSWORD_TOO_SHORT").Code);
        Assert.Equal(CkMethodErrorCodes.MethodForbidden, new CkMethodFailure(CkMethodErrorCodes.MethodForbidden).Code);
        Assert.Throws<ArgumentException>(() => new CkMethodFailure("lower_case"));
        Assert.Throws<ArgumentException>(() => new CkMethodFailure("METHOD_MY_OWN"));
        Assert.Throws<ArgumentException>(() => new CkMethodFailure(""));
    }

    [Fact]
    public void Outcome_ConvertsImplicitlyFromTheResult()
    {
        CkMethodOutcome<UserChangePasswordResult> outcome = new UserChangePasswordResult { Value = "x" };

        Assert.True(outcome.IsSuccess);
        Assert.Equal("x", outcome.Value!.Value);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void Outcome_ConvertsImplicitlyFromAFailure()
    {
        CkMethodOutcome<UserChangePasswordResult> outcome = new CkMethodFailure("SOME_ERROR");

        Assert.False(outcome.IsSuccess);
        Assert.Equal("SOME_ERROR", outcome.Failure!.Code);
    }

    [Fact]
    public void ContextToString_PrintsNeitherParametersNorToken()
    {
        var request = TestData.Request(DateTime.UtcNow.AddSeconds(5));
        var context = new CkMethodContext<UserChangePasswordParameters>(request,
            new UserChangePasswordParameters { CurrentPassword = TestData.OldSecret, NewPassword = TestData.Secret });

        var text = context.ToString();

        Assert.DoesNotContain(TestData.Secret, text);
        Assert.DoesNotContain(TestData.OldSecret, text);
        Assert.DoesNotContain(TestData.Token, text);
        Assert.DoesNotContain(TestData.Secret, request.ToString());
        Assert.Contains(TestData.MethodId, text);
    }

    [Fact]
    public void Json_UsesCamelCase_AndEnumNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { Name = "x", Kind = DayOfWeek.Monday },
            CkMethodJson.Options);

        Assert.Equal("{\"name\":\"x\",\"kind\":\"Monday\"}", json);
        Assert.Equal(DayOfWeek.Tuesday,
            System.Text.Json.JsonSerializer.Deserialize<DayOfWeek>("2", CkMethodJson.Options));
    }
}
