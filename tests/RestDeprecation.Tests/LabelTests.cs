using System.Security.Claims;
using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace RestDeprecation.Tests;

/// <summary>AB#8421: label values of <c>octo.rest.deprecated.calls</c> stay bounded and free of request data.</summary>
public class LabelTests
{
    [Theory]
    [InlineData(100, "1xx")]
    [InlineData(200, "2xx")]
    [InlineData(204, "2xx")]
    [InlineData(302, "3xx")]
    [InlineData(400, "4xx")]
    [InlineData(403, "4xx")]
    [InlineData(500, "5xx")]
    [InlineData(599, "5xx")]
    [InlineData(0, "other")]
    [InlineData(600, "other")]
    public void StatusClass(int status, string expected)
    {
        Assert.Equal(expected, RestDeprecationMetrics.StatusClassLabel(status));
    }

    [Fact]
    public void Endpoint_WithMethodRestriction_UsesRequestMethodAndTemplate()
    {
        var endpoint = Endpoint("{tenantId}/v1/users/{userName}", new HttpMethodMetadata(["GET"]));

        Assert.Equal("GET /{tenantId}/v1/users/{userName}", RestDeprecationMetrics.EndpointLabel("get", endpoint));
    }

    [Fact]
    public void Endpoint_WithoutMethodRestriction_DoesNotReportTheCallersMethod()
    {
        var endpoint = Endpoint("/{tenantId}/v1/anything");

        Assert.Equal("ANY /{tenantId}/v1/anything", RestDeprecationMetrics.EndpointLabel("X-FORGED-METHOD", endpoint));
    }

    [Fact]
    public void ClientId_Unauthenticated_IsAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "forged")]));

        Assert.Equal("anonymous", RestDeprecationMetrics.ClientIdLabel(principal));
        Assert.Equal("anonymous", RestDeprecationMetrics.ClientIdLabel(null));
    }

    [Fact]
    public void ClientId_AuthenticatedWithoutClaim_IsAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "s")], "Bearer"));

        Assert.Equal("anonymous", RestDeprecationMetrics.ClientIdLabel(principal));
    }

    [Theory]
    [InlineData("octo-pipeline-sa-65f1c2a9e4b0d3a1c8e7f210")]
    [InlineData("OCTO-PIPELINE-SA-65F1C2A9E4B0D3A1C8E7F211")]
    public void ClientId_PipelineServiceAccount_IsReportedWithoutAdapterId(string clientId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", clientId)], "Bearer"));

        Assert.Equal("octo-pipeline-sa", RestDeprecationMetrics.ClientIdLabel(principal));
    }

    [Fact]
    public void ClientId_PipelineServiceAccount_TooLong_IsStillTheSharedLabel()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", "octo-pipeline-sa-" + new string('t', RestDeprecationMetrics.MaxClientIdLength))],
            "Bearer"));

        Assert.Equal("octo-pipeline-sa", RestDeprecationMetrics.ClientIdLabel(principal));
    }

    [Fact]
    public void ClientId_TooLong_IsOther()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", new string('c', RestDeprecationMetrics.MaxClientIdLength + 1))], "Bearer"));

        Assert.Equal("other", RestDeprecationMetrics.ClientIdLabel(principal));
    }

    private static RouteEndpoint Endpoint(string template, params object[] metadata)
    {
        return new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0,
            new EndpointMetadataCollection(metadata), "test endpoint");
    }
}
