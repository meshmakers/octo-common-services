using Xunit;

namespace RestDeprecation.Tests;

/// <summary>AB#8421: an invalid attribute fails the service at startup with a message naming the action.</summary>
public class StartupValidationTests
{
    [Theory]
    [InlineData(typeof(SunsetNotAfterSinceController), "SunsetNotAfterSinceController.SameDay",
        "Sunset 2027-01-01 must be after Since 2027-01-01")]
    [InlineData(typeof(RelativeLinkController), "RelativeLinkController.RelativeLink",
        "Link '/docs/methods' is not an absolute http(s) URL")]
    [InlineData(typeof(BadDateController), "BadDateController.BadDate",
        "Since '01.12.2026' is not a date in the format yyyy-MM-dd")]
    public async Task InvalidAttribute_FailsStartup_NamingTheAction(Type controller, string action, string reason)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = await TestApp.StartAsync(controllers: controller);
        });

        Assert.Contains("[OctoRestDeprecated]", exception.Message);
        Assert.Contains(action, exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task ValidAttributes_StartNormally()
    {
        await using var app = await TestApp.StartAsync(controllers:
            [typeof(UsersTestController), typeof(LegacyTestController)]);
    }
}
