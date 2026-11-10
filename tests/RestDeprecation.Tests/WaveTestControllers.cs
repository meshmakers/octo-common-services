using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace RestDeprecation.Tests;

/// <summary>Wave names and the configuration of the wave tests (AB#8415).</summary>
internal static class TestWave
{
    public const string Dated = "Dated";
    public const string SinceOnly = "SinceOnly";
    public const string Undated = "Undated";

    /// <summary>
    ///     Configuration as a cluster sets it through <c>OCTO_RestDeprecation__Waves__…</c>; the keys use another casing
    ///     than the attributes on purpose, as environment variables often do.
    /// </summary>
    public static readonly Dictionary<string, string?> Configuration = new()
    {
        ["RestDeprecation:Waves:DATED:Since"] = TestDeprecation.Since,
        ["RestDeprecation:Waves:DATED:Sunset"] = TestDeprecation.Sunset,
        ["RestDeprecation:Waves:sinceonly:Since"] = TestDeprecation.Since,
        // Helm renders an unset value as an empty string: that is "not configured", not an error.
        ["RestDeprecation:Waves:sinceonly:Sunset"] = ""
    };

    public static Action<WebApplicationBuilder> Configure(IReadOnlyDictionary<string, string?> values)
    {
        return builder => builder.Configuration.AddInMemoryCollection(values);
    }
}

/// <summary>Operations of one service in three waves: dated, since only and not yet dated.</summary>
[ApiController]
[Route("{tenantId}/v1/wave")]
public class WaveTestController : ControllerBase
{
    [HttpPut("{userName}/dated")]
    [EndpointDescription("Replaces the roles of a user.")]
    [OctoRestDeprecated(Wave = TestWave.Dated, Link = TestDeprecation.Link)]
    public IActionResult Dated(string tenantId, string userName) => Ok();

    [HttpPut("{userName}/since-only")]
    [OctoRestDeprecated(Wave = TestWave.SinceOnly, Link = TestDeprecation.Link)]
    public IActionResult SinceOnly(string tenantId, string userName) => Ok();

    [HttpPut("{userName}/undated")]
    [OctoRestDeprecated(Wave = TestWave.Undated, Link = TestDeprecation.Link, Successor = TestDeprecation.Successor)]
    public IActionResult Undated(string tenantId, string userName) => Ok();
}

[ApiController]
[Route("invalid")]
public class WaveWithDatesController : ControllerBase
{
    [HttpGet("wave-with-dates")]
    [OctoRestDeprecated(Wave = TestWave.Dated, Since = TestDeprecation.Since, Link = TestDeprecation.Link)]
    public IActionResult WaveWithDates() => Ok();
}

[ApiController]
[Route("invalid")]
public class BadWaveNameController : ControllerBase
{
    [HttpGet("bad-wave-name")]
    [OctoRestDeprecated(Wave = "identity-users-1", Link = TestDeprecation.Link)]
    public IActionResult BadWaveName() => Ok();
}

[ApiController]
[Route("invalid")]
public class WaveWithoutLinkController : ControllerBase
{
    [HttpGet("wave-without-link")]
    [OctoRestDeprecated(Wave = TestWave.Undated)]
    public IActionResult WaveWithoutLink() => Ok();
}

/// <summary>Valid attribute; the tests give its wave an invalid configuration.</summary>
[ApiController]
[Route("{tenantId}/v1/configured")]
public class ConfiguredWaveController : ControllerBase
{
    [HttpGet("op")]
    [OctoRestDeprecated(Wave = "Configured", Link = TestDeprecation.Link)]
    public IActionResult Operation() => Ok();
}
