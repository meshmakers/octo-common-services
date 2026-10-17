using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Xunit;

namespace Contracts.Tests.Methods;

public class MethodErrorCodesTests
{
    [Fact]
    public void EveryPlatformCode_IsUnique_UpperSnakeCase_AndUsesTheReservedPrefix()
    {
        Assert.Equal(CkMethodErrorCodes.All.Count, CkMethodErrorCodes.All.Distinct().Count());
        Assert.All(CkMethodErrorCodes.All, c =>
        {
            Assert.Matches("^METHOD_[A-Z]+(_[A-Z]+)*$", c);
            Assert.True(CkMethodErrorCodes.IsPlatformCode(c));
            Assert.False(CkMethodErrorCodes.IsValidDeclaredCode(c));
        });
    }

    [Fact]
    public void Catalog_DocumentsEveryConstant_WithTextAndActionAndKnownParameters()
    {
        var constants = typeof(CkMethodErrorCodes).GetFields()
            .Where(f => f.IsLiteral && f.Name != nameof(CkMethodErrorCodes.PlatformPrefix))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var knownParameters = typeof(CkMethodErrorParameters).GetFields()
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();

        Assert.Equal(constants, CkMethodErrorCodes.Catalog.Select(c => c.Code).ToHashSet());
        Assert.All(CkMethodErrorCodes.Catalog, info =>
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Description), info.Code);
            Assert.False(string.IsNullOrWhiteSpace(info.CallerAction), info.Code);
            Assert.All(info.ParameterNames, p => Assert.Contains(p, knownParameters));
        });
    }

    [Fact]
    public void ContractDocument_ListsEveryPlatformCode_AndEveryWellKnownParameter()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "method-wire-contract.md"));

        Assert.All(CkMethodErrorCodes.All, code => Assert.Contains($"`{code}`", text));
        Assert.All(typeof(CkMethodErrorParameters).GetFields().Where(f => f.IsLiteral),
            f => Assert.Contains($"`{f.GetRawConstantValue()}`", text));
    }

    [Theory]
    [InlineData("PASSWORD_TOO_SHORT", true)]
    [InlineData("E42", true)]
    [InlineData("METHOD_TIMEOUT", false)]
    [InlineData("METHOD_MY_OWN", false)]
    [InlineData("method_lower", false)]
    [InlineData("HAS SPACE", false)]
    [InlineData("AB", false)]
    [InlineData("", false)]
    public void DeclaredCodes_MustNotUseTheReservedPrefix(string code, bool valid) =>
        Assert.Equal(valid, CkMethodErrorCodes.IsValidDeclaredCode(code));

    [Fact]
    public void Error_Create_BuildsParameters()
    {
        var error = CkMethodError.Create(CkMethodErrorCodes.MethodValidationFailed,
            (CkMethodErrorParameters.Field, "newPassword"));

        Assert.Equal("newPassword", error.Parameters[CkMethodErrorParameters.Field]);
    }

    [Theory]
    [InlineData("System.Identity/User.ChangePassword-1", "octo::method::system.identity::user::changepassword-1")]
    [InlineData("System.Identity-2.90.0/User.ChangePassword-1", "octo::method::system.identity::user::changepassword-1")]
    [InlineData("System.Identity@2/User.ChangePassword-2", "octo::method::system.identity::user::changepassword-2")]
    [InlineData("Basic/Asset.Sub.Reset-3", "octo::method::basic::asset.sub::reset-3")]
    public void QueueNames_For_BuildsLowerCaseVersionLessName(string methodId, string expected) =>
        Assert.Equal(expected, CkMethodQueueNames.For(methodId));

    [Theory]
    [InlineData("User.ChangePassword-1")]
    [InlineData("System.Identity/User")]
    [InlineData("System.Identity/User.ChangePassword")]
    [InlineData("System.Identity/User.")]
    [InlineData("/User.ChangePassword-1")]
    [InlineData(" ")]
    public void QueueNames_For_InvalidId_Throws(string methodId) =>
        Assert.ThrowsAny<ArgumentException>(() => CkMethodQueueNames.For(methodId));

    [Theory]
    [InlineData("System.Identity/User.ChangePassword-1", "System.Identity")]
    [InlineData("System.Identity-2.90.0/User.ChangePassword-1", "System.Identity")]
    [InlineData("System.Identity@2/User.ChangePassword-1", "System.Identity")]
    public void QueueNames_GetModelName_StripsVersion(string methodId, string expected) =>
        Assert.Equal(expected, CkMethodQueueNames.GetModelName(methodId));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Octo.Common.Services.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
