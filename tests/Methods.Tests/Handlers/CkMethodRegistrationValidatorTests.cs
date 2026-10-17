using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Methods.Handlers;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Handlers;

public class CkMethodRegistrationValidatorTests
{
    private static void Validate<THandler>(CkMethodDto? definition, string methodId = TestData.MethodId,
        params string[] tenants)
        where THandler : class, ICkMethodHandler
    {
        var registrations = new CkMethodHandlerRegistrations();
        registrations.SetServiceName("TestService");
        registrations.Add(methodId, typeof(THandler));
        new CkMethodRegistrationValidator(new FixedDefinitionSource(definition, tenants)).Validate(registrations);
        Registration = registrations.Handlers[methodId];
    }

    private static CkMethodRegistration Registration { get; set; } = null!;

    [Fact]
    public void MatchingDefinition_Passes_AndRecordsSensitiveParametersAndDeclaredErrors()
    {
        Validate<ChangePasswordHandler>(TestData.Definition(errors: "PASSWORD_POLICY_VIOLATION"));

        Assert.Equal(["currentPassword", "newPassword"], Registration.SensitiveParameters.Order());
        Assert.Equal("PASSWORD_POLICY_VIOLATION", Assert.Single(Registration.Definition!.Errors!).Code);
    }

    [Fact]
    public void UnknownMethod_FailsStartup_NamingTheMethodAndTheSearchedTenants()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Validate<ChangePasswordHandler>(null, "Acme.Model/Thing.DoIt-1", "octosystem", "t2"));

        Assert.Contains("Acme.Model/Thing.DoIt-1", e.Message);
        Assert.Contains("'octosystem'", e.Message);
        Assert.Contains("'t2'", e.Message);
        Assert.Contains(nameof(ChangePasswordHandler), e.Message);
    }

    [Fact]
    public void ParametersTypeWithoutAParameterProperty_FailsStartup_NamingMethodAndParameter()
    {
        var definition = TestData.Definition();
        definition.Parameters!.Add(new CkMethodParameterDto { Name = "mfaCode", ValueType = AttributeValueTypesDto.String });

        var e = Assert.Throws<InvalidOperationException>(() => Validate<ChangePasswordHandler>(definition));

        Assert.Contains(TestData.MethodId, e.Message);
        Assert.Contains("'mfaCode'", e.Message);
    }

    [Fact]
    public void ParameterOfAnotherValueType_FailsStartup()
    {
        var definition = TestData.Definition();
        definition.Parameters![0].ValueType = AttributeValueTypesDto.Int;

        var e = Assert.Throws<InvalidOperationException>(() => Validate<ChangePasswordHandler>(definition));

        Assert.Contains("'currentPassword'", e.Message);
        Assert.Contains("Int", e.Message);
    }

    [Fact]
    public void RequiredPropertyWithoutAParameter_FailsStartup()
    {
        var definition = TestData.Definition();
        definition.Parameters!.RemoveAt(1); // newPassword is required on the record

        var e = Assert.Throws<InvalidOperationException>(() => Validate<ChangePasswordHandler>(definition));

        Assert.Contains("NewPassword", e.Message);
    }

    [Fact]
    public void MethodWithResult_ButHandlerWithoutResult_FailsStartup()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Validate<NoResultHandler>(TestData.Definition(withResult: true)));

        Assert.Contains(TestData.MethodId, e.Message);
        Assert.Contains("declares a result", e.Message);
    }

    [Fact]
    public void MethodWithoutResult_ButHandlerWithResult_FailsStartup()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Validate<ChangePasswordHandler>(TestData.Definition(withResult: false)));

        Assert.Contains("declares no result", e.Message);
    }
}
