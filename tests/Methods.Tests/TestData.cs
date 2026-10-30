using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Tests;

/// <summary>Records shaped like the generated <c>{Type}{Method}Parameters</c> / <c>Result</c> records.</summary>
public sealed record UserChangePasswordParameters
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }

    public string? Hint { get; init; }
}

public sealed record UserChangePasswordResult
{
    public string? Value { get; init; }
}

internal static class TestData
{
    public const string MethodId = "System.Identity/User.ChangePassword-1";
    public const string Secret = "S3cr3t-Pa55word!";
    public const string OldSecret = "0ld-Pa55word?";
    public const string Token = "eyJhbGciOiJSUzI1NiJ9.payload.signature";

    public static CkMethodRequest Request(DateTime deadlineUtc, string tenantId = "meshtest",
        string? parametersJson = null, int contractVersion = CkMethodContract.CurrentVersion) =>
        new(tenantId)
        {
            ContractVersion = contractVersion,
            InvocationId = Guid.NewGuid(),
            MethodId = MethodId,
            CkTypeId = "System.Identity/User",
            TargetRtId = "65f0c0ffee",
            ParametersJson = parametersJson ??
                             $"{{\"currentPassword\":\"{OldSecret}\",\"newPassword\":\"{Secret}\"}}",
            CallerAccessToken = Token,
            Caller = new CkMethodCaller { SubjectId = "sub-1" },
            DeadlineUtc = deadlineUtc
        };

    /// <summary>The definition of <see cref="Definition" /> with a declared authorization (<c>roles</c>, any-of).</summary>
    public static CkMethodDto DefinitionWithRoles(params string[] roles)
    {
        var definition = Definition();
        definition.Authorization = new CkMethodAuthorizationDto { Roles = roles.ToList() };
        return definition;
    }

    /// <summary>A definition whose parameters are all non-sensitive.</summary>
    public static CkMethodDto DefinitionWithoutSensitive() => new()
    {
        MethodId = "ChangePassword-1",
        Parameters = [new CkMethodParameterDto { Name = "hint", ValueType = AttributeValueTypesDto.String, IsOptional = true }],
        Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.String }
    };

    /// <summary>The CK definition the generated records above belong to.</summary>
    public static CkMethodDto Definition(bool withResult = true, params string[] errors) => new()
    {
        MethodId = "ChangePassword-1",
        Parameters =
        [
            new CkMethodParameterDto { Name = "currentPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "newPassword", ValueType = AttributeValueTypesDto.String, Sensitive = true },
            new CkMethodParameterDto { Name = "hint", ValueType = AttributeValueTypesDto.String, IsOptional = true }
        ],
        Result = withResult ? new CkMethodResultDto { ValueType = AttributeValueTypesDto.String } : null,
        Errors = errors.Select(e => new CkMethodErrorDto { Code = e }).ToList()
    };
}
