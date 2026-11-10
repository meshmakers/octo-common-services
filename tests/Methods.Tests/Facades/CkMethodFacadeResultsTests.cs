using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Facades;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Meshmakers.Octo.Services.Methods.Tests.Facades;

/// <summary>AB#8411: the REST facade helper maps the method envelope to an ASP.NET result.</summary>
public class CkMethodFacadeResultsTests
{
    private static readonly Guid Invocation = Guid.NewGuid();

    private static CkMethodResponse Failed(string code, params (string, string)[] parameters) =>
        CkMethodResponse.Failed(Invocation, "h1", CkMethodError.Create(code, parameters));

    public static TheoryData<string, int> PlatformCodes => new()
    {
        { CkMethodErrorCodes.MethodForbidden, 403 },
        { CkMethodErrorCodes.MethodTargetNotFound, 404 },
        { CkMethodErrorCodes.MethodValidationFailed, 400 },
        { CkMethodErrorCodes.MethodParametersTooLarge, 413 },
        { CkMethodErrorCodes.MethodRequestInProgress, 409 },
        { CkMethodErrorCodes.MethodTimeout, 504 },
        { CkMethodErrorCodes.MethodHandlerNotAvailable, 503 },
        { CkMethodErrorCodes.MethodContractVersionUnsupported, 503 },
        { CkMethodErrorCodes.MethodNotSupportedByHandlerVersion, 503 },
        { CkMethodErrorCodes.MethodHandlerFailed, 500 },
        { CkMethodErrorCodes.MethodOutcomeUnknown, 500 }
    };

    [Theory]
    [MemberData(nameof(PlatformCodes))]
    public void PlatformCode_GetsItsDefaultStatus_AndAProblemBodyWithCodeAndParameters(string code, int status)
    {
        var result = Assert.IsType<ObjectResult>(Failed(code, (CkMethodErrorParameters.MethodId, "M-1")).ToActionResult());

        Assert.Equal(status, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(status, problem.Status);
        Assert.Equal(code, problem.Title);
        Assert.Equal(code, problem.Extensions["code"]);
        var parameters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(problem.Extensions["parameters"]);
        Assert.Equal("M-1", parameters[CkMethodErrorParameters.MethodId]);
        Assert.False(problem.Extensions.ContainsKey("details"));
    }

    [Fact]
    public void EveryPlatformCodeOfTheCatalog_HasADocumentedDefault()
    {
        Assert.All(CkMethodErrorCodes.Catalog, info => Assert.True(
            CkMethodFacadeResults.DefaultStatusCodes.ContainsKey(info.Code), $"no default status for {info.Code}"));
    }

    [Fact]
    public void UnknownPlatformCode_Is500_DeclaredCode_Is400()
    {
        Assert.Equal(500, CkMethodFacadeResults.StatusCodeFor("METHOD_SOMETHING_NEW"));
        Assert.Equal(400, CkMethodFacadeResults.StatusCodeFor("PASSWORD_TOO_SHORT"));
        Assert.Equal(400, Assert.IsType<ObjectResult>(Failed("PASSWORD_TOO_SHORT").ToActionResult()).StatusCode);
    }

    [Fact]
    public void Details_AreInTheBody()
    {
        var response = CkMethodResponse.Failed(Invocation, "h1", CkMethodError.Create("PASSWORD_POLICY_VIOLATED") with
        {
            Details = [new CkMethodErrorDetail { Code = "TOO_SHORT" }]
        });

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(response.ToActionResult()).Value);

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CkMethodErrorDetail>>(problem.Extensions["details"]));
    }

    [Fact]
    public void Override_ForADeclaredCode_WithStatusOrResult()
    {
        var options = new CkMethodFacadeOptions()
            .MapError("USER_NOT_FOUND", 404)
            .MapError("PASSWORD_POLICY_VIOLATED", e => new BadRequestObjectResult(new { reason = e.Code }));

        Assert.Equal(404, Assert.IsType<ObjectResult>(Failed("USER_NOT_FOUND").ToActionResult(options)).StatusCode);
        Assert.IsType<BadRequestObjectResult>(Failed("PASSWORD_POLICY_VIOLATED").ToActionResult(options));
        // a code without an override keeps its default
        Assert.Equal(403, Assert.IsType<ObjectResult>(Failed(CkMethodErrorCodes.MethodForbidden).ToActionResult(options)).StatusCode);
    }

    [Fact]
    public void Override_ForAPlatformCode()
    {
        var options = new CkMethodFacadeOptions().MapError(CkMethodErrorCodes.MethodForbidden, _ => new UnauthorizedResult());

        Assert.IsType<UnauthorizedResult>(Failed(CkMethodErrorCodes.MethodForbidden).ToActionResult(options));
    }

    [Fact]
    public void Success_WithResult_Is200Json_WithoutResult_Is204()
    {
        var content = Assert.IsType<ContentResult>(CkMethodResponse.Succeeded(Invocation, "h1", "{\"a\":1}").ToActionResult());
        Assert.Equal(200, content.StatusCode);
        Assert.Equal("application/json", content.ContentType);
        Assert.Equal("{\"a\":1}", content.Content);

        Assert.IsType<NoContentResult>(CkMethodResponse.Succeeded(Invocation, "h1").ToActionResult());
    }

    [Fact]
    public void Accepted_Is202_WithTheOperationHandle()
    {
        var handle = new CkMethodOperationHandle { OperationId = "op-1", RetryAfterSeconds = 5 };

        var result = Assert.IsType<ObjectResult>(CkMethodResponse.Accepted(Invocation, "h1", handle).ToActionResult());

        Assert.Equal(202, result.StatusCode);
        Assert.Same(handle, result.Value);
    }

    [Fact]
    public void Success_Override()
    {
        var options = new CkMethodFacadeOptions().MapSuccess(r => new OkObjectResult(r.InvocationId));

        var result = Assert.IsType<OkObjectResult>(CkMethodResponse.Succeeded(Invocation, "h1", "1").ToActionResult(options));

        Assert.Equal(Invocation, result.Value);
    }

    [Fact]
    public void FailedEnvelopeWithoutError_IsHandlerFailed500()
    {
        var response = new CkMethodResponse { InvocationId = Invocation, HandlerInstanceId = "h1", Success = false };

        var result = Assert.IsType<ObjectResult>(response.ToActionResult());

        Assert.Equal(500, result.StatusCode);
        Assert.Equal(CkMethodErrorCodes.MethodHandlerFailed, Assert.IsType<ProblemDetails>(result.Value).Title);
    }

    [Fact]
    public void MapError_RejectsAnEmptyCode()
    {
        Assert.Throws<ArgumentException>(() => new CkMethodFacadeOptions().MapError(" ", 400));
    }
}
