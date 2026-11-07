using System.ComponentModel.DataAnnotations;
using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace RestDeprecation.Tests;

internal static class TestDeprecation
{
    public const string Since = "2026-12-01";
    public const string Sunset = "2027-03-01";
    public const string Link = "https://docs.meshmakers.cloud/methods/user-set-password";
    public const string Successor = "https://docs.meshmakers.cloud/methods/user-set-password/v2";

    // Pinned literals, not computed: the test is about the wire format.
    public const string DeprecationHeader = "@1796083200";
    public const string SunsetHeader = "Mon, 01 Mar 2027 00:00:00 GMT";
    public const string LinkHeader = "<https://docs.meshmakers.cloud/methods/user-set-password>; rel=\"deprecation\"; type=\"text/html\"";
    public const string SuccessorLinkHeader = "<https://docs.meshmakers.cloud/methods/user-set-password/v2>; rel=\"successor-version\"";
}

/// <summary>Actions of a service that migrates one operation (marked) and keeps another (unmarked).</summary>
[ApiController]
[Route("{tenantId}/v1/users")]
public class UsersTestController : ControllerBase
{
    /// <summary>Deprecated read: 200, 400 (model validation) or 403 (authorization).</summary>
    [HttpGet("{userName}/password-state")]
    [EndpointDescription("Returns the password state of a user.")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link)]
    public IActionResult GetPasswordState(string tenantId, string userName, [FromQuery] [Range(0, 10)] int detail = 0)
    {
        return Ok(new { tenantId, userName, detail });
    }

    /// <summary>Deprecated administration operation, behind a role policy.</summary>
    [HttpPost("{userName}/reset-password")]
    [Authorize(TestApp.AdminPolicy)]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link)]
    public IActionResult ResetPassword(string tenantId, string userName)
    {
        return NoContent();
    }

    /// <summary>Deprecated operation that fails.</summary>
    [HttpGet("{userName}/explode")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link)]
    public IActionResult Explode(string tenantId, string userName)
    {
        throw new InvalidOperationException("boom");
    }

    /// <summary>Deprecated operation with a successor link (RFC 5829).</summary>
    [HttpGet("{userName}/with-successor")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link,
        Successor = TestDeprecation.Successor)]
    public IActionResult WithSuccessor(string tenantId, string userName)
    {
        return Ok(new { tenantId, userName });
    }

    /// <summary>The operation that stays.</summary>
    [HttpGet("{userName}")]
    public IActionResult GetUser(string tenantId, string userName)
    {
        return Ok(new { tenantId, userName });
    }

    /// <summary>Unmarked operation that fails.</summary>
    [HttpGet("{userName}/unmarked-explode")]
    public IActionResult UnmarkedExplode(string tenantId, string userName)
    {
        throw new InvalidOperationException("boom");
    }
}

/// <summary>Attribute on the controller; one action overrides the dates.</summary>
[ApiController]
[Route("{tenantId}/v1/legacy")]
[OctoRestDeprecated(Since = "2026-01-01", Sunset = "2026-06-01", Link = "https://docs.meshmakers.cloud/legacy")]
public class LegacyTestController : ControllerBase
{
    [HttpGet("inherited")]
    public IActionResult Inherited() => Ok();

    [HttpGet("overridden")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link)]
    public IActionResult Overridden() => Ok();
}

[ApiController]
[Route("invalid")]
public class SunsetNotAfterSinceController : ControllerBase
{
    [HttpGet("same-day")]
    [OctoRestDeprecated(Since = "2027-01-01", Sunset = "2027-01-01", Link = TestDeprecation.Link)]
    public IActionResult SameDay() => Ok();
}

[ApiController]
[Route("invalid")]
public class RelativeLinkController : ControllerBase
{
    [HttpGet("relative-link")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = "/docs/methods")]
    public IActionResult RelativeLink() => Ok();
}

[ApiController]
[Route("invalid")]
public class BadDateController : ControllerBase
{
    [HttpGet("bad-date")]
    [OctoRestDeprecated(Since = "01.12.2026", Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link)]
    public IActionResult BadDate() => Ok();
}

[ApiController]
[Route("invalid")]
public class NonAsciiLinkController : ControllerBase
{
    [HttpGet("non-ascii-link")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset,
        Link = "https://docs.meshmakers.cloud/methods/\u00fcbersicht")]
    public IActionResult NonAsciiLink() => Ok();
}

[ApiController]
[Route("invalid")]
public class RelativeSuccessorController : ControllerBase
{
    [HttpGet("relative-successor")]
    [OctoRestDeprecated(Since = TestDeprecation.Since, Sunset = TestDeprecation.Sunset, Link = TestDeprecation.Link,
        Successor = "/v2/methods")]
    public IActionResult RelativeSuccessor() => Ok();
}
