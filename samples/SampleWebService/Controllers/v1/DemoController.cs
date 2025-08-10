using Asp.Versioning;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleWebService.Models.v1;

namespace SampleWebService.Controllers.v1;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route(SampleServiceConstants.ApiPathPrefix + "/[controller]")]
[ApiVersion(SampleServiceConstants.ApiVersion1)]
public class DemoController(ILogger<DemoController> logger) : ControllerBase
{
    [HttpGet("GetDemoValues")]
    [EndpointSummary("Get demo values")]
    [EndpointDescription("Some description")]
    [ProducesResponseType(typeof(DemoValueV1[]), 200)]
    [ProducesResponseType(typeof(ApiErrorDto), 400)]
    public IEnumerable<DemoValueV1> Get()
    {
        logger.LogInformation("Getting demo values v1");
        
        return Enumerable.Range(1, 5).Select(_ => new DemoValueV1
            {
                Value = Random.Shared.Next(-20, 55),
            })
            .ToArray();
    }
}