using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Mvc;

namespace SampleWebService.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<WeatherForecastController> _logger;

    public WeatherForecastController(ILogger<WeatherForecastController> logger)
    {
        _logger = logger;
    }

    [HttpGet(Name = "GetWeatherForecast")]
    public IEnumerable<WeatherForecast> Get()
    {
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
    }

    /// <summary>
    ///     Sample of a deprecated operation (AB#8421, docs/rest-deprecation.md): every response carries the Deprecation,
    ///     Sunset and Link headers, the OpenAPI operation is deprecated and each call is counted on
    ///     octo.rest.deprecated.calls.
    /// </summary>
    [HttpGet("legacy", Name = "GetLegacyWeatherForecast")]
    [OctoRestDeprecated(Since = "2026-12-01", Sunset = "2027-03-01",
        Link = "https://docs.meshmakers.cloud/docs/technologyGuide/constructionKits/ckLanguage2")]
    public IEnumerable<WeatherForecast> GetLegacy()
    {
        return Get();
    }
}
