using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SampleWebService.Models.v2;

namespace SampleWebService.Controllers.v2;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route(SampleServiceConstants.ApiPathPrefix + "/[controller]")]
[ApiVersion(SampleServiceConstants.ApiVersion2)]
public class DemoController(ILogger<DemoController> logger) : ControllerBase
{
    [HttpGet("GetDemoValues")]
    public IEnumerable<DemoValueV2> Get()
    {
        logger.LogInformation("Getting demo values v2");
        
        return Enumerable.Range(1, 5).Select(_ => new DemoValueV2
            {
                TimeStamp = DateTime.UtcNow.AddMinutes(Random.Shared.Next(0, 120)),
                Value = Random.Shared.Next(-20, 55),
            })
            .ToArray();
    }
}