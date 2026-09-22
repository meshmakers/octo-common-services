using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Xunit;

namespace Notifications.Tests;

/// <summary>
///     Covers what <see cref="NotificationService" /> puts on the wire — the producer side of the
///     contract pinned by <see cref="NotificationWireContractTests" />.
/// </summary>
public class NotificationServiceTests
{
    private const string TenantId = "test-tenant";
    private const string TemplateName = "System.Identity.OneTimePassword";
    private const string SubjectId = "6501f2a3b4c5d6e7f8a9b0c1";
    private const string CorrelationRtId = "6501f2a3b4c5d6e7f8a9b0c2";
    private const string LiteralRecipient = "recipient@example.com";

    private const string SubjectTemplate = "subject-template";
    private const string BodyTemplate = "body-template";

    // What the faked renderer returns, so the assertions can tell the two render paths apart:
    // the subject always goes through RenderPlainText, an Html template's body through RenderHtml.
    private const string ExpectedSubject = "plain:" + SubjectTemplate;
    private const string ExpectedBody = "html:" + BodyTemplate;

    [Theory]
    // forcedChannel and correlationRtId are optional. Both omitted is the ordinary case; both given
    // is the OTP case, where the channel must not be left to the recipient's preference.
    [InlineData(null, null)]
    [InlineData("SIGNAL", CorrelationRtId)]
    public async Task SendToUserAsync_AddressesTheRecipientBySubjectIdAndLeavesTheAddressUnresolved(
        string? forcedChannel, string? correlationRtId)
    {
        var harness = new Harness();

        await harness.Service.SendToUserAsync(TenantId, SubjectId, TemplateName,
            forcedChannel: forcedChannel, correlationRtId: correlationRtId);

        var request = harness.CapturedRequest();
        Assert.Equal(TenantId, request.TenantId);

        var notification = Assert.Single(request.Notifications);

        Assert.Equal(SubjectId, notification.RecipientSubjectId);

        // The load-bearing assertion. The address is deliberately NOT resolved here — it is not
        // known at this layer and must not be guessed. ResolveNotificationChannel@1 (AB#5152) fills
        // in the delivery target at send time from the recipient's verified bindings, falling back
        // to the directory e-mail. A well-meant default here ("just put the user's e-mail in") would
        // pin every notification to e-mail and silently defeat channel resolution: the message would
        // still be delivered, just never on Signal or Teams, and nothing would report an error.
        Assert.Null(notification.Recipient);
        Assert.Null(notification.Cc);
        Assert.Null(notification.Bcc);

        Assert.Equal(TemplateName, notification.TemplateName);
        Assert.Equal(forcedChannel, notification.ForcedChannel);
        Assert.Equal(correlationRtId, notification.CorrelationRtId);

        // Rendering is shared with SendAsync via RenderAsync — pinned on both paths.
        Assert.Equal(ExpectedSubject, notification.Subject);
        Assert.Equal(ExpectedBody, notification.Body);
    }

    /// <summary>
    ///     Companion to the test above: the address-addressed path still addresses a literal e-mail
    ///     address, and of the AB#5217 members carries only <c>TemplateName</c>.
    /// </summary>
    [Fact]
    public async Task SendAsync_KeepsTheLiteralRecipientAndCarriesOnlyTheTemplateName()
    {
        var harness = new Harness();

        await harness.Service.SendAsync(TenantId, TemplateName, LiteralRecipient,
            cc: "cc@example.com", bcc: "bcc@example.com");

        var request = harness.CapturedRequest();
        Assert.Equal(TenantId, request.TenantId);

        var notification = Assert.Single(request.Notifications);

        Assert.Equal(LiteralRecipient, notification.Recipient);
        Assert.Equal("cc@example.com", notification.Cc);
        Assert.Equal("bcc@example.com", notification.Bcc);

        // Carried since AB#5223 so the delivery record of identity's own mails — welcome, confirm,
        // reset-password, all of which come through here — names the template that produced them.
        Assert.Equal(TemplateName, notification.TemplateName);

        // RecipientSubjectId must stay null on this path. The caller named an address, not a user,
        // so there is no identity to resolve; inventing one would hand the delivery pipeline a
        // recipient to do channel resolution against and route the message somewhere the caller
        // never asked for. ForcedChannel and CorrelationRtId are meaningless without a user and an
        // originating event respectively.
        Assert.Null(notification.RecipientSubjectId);
        Assert.Null(notification.ForcedChannel);
        Assert.Null(notification.CorrelationRtId);

        // Same shared RenderAsync as SendToUserAsync: a regression there has to fail both tests.
        Assert.Equal(ExpectedSubject, notification.Subject);
        Assert.Equal(ExpectedBody, notification.Body);
    }

    /// <summary>
    ///     The template lookup chain <see cref="NotificationService" /> needs:
    ///     <see cref="ISystemContext.FindTenantRepositoryAsync" /> → <c>GetSessionAsync</c> →
    ///     <c>GetRtEntitiesByTypeAsync&lt;RtNotificationTemplate&gt;</c>, plus a renderer whose output
    ///     identifies which of the two render methods produced it, plus the published request.
    /// </summary>
    private sealed class Harness
    {
        private SendNotificationsRequest? _published;

        public Harness()
        {
            var template = new RtNotificationTemplate
            {
                RtWellKnownName = TemplateName,
                SubjectTemplate = SubjectTemplate,
                BodyTemplate = BodyTemplate,
                RenderingType = RtRenderingTypesEnum.Html
            };

            var resultSet = A.Fake<IResultSet<RtNotificationTemplate>>();
            A.CallTo(() => resultSet.TotalCount).Returns(1L);
            A.CallTo(() => resultSet.Items).Returns(new[] { template });

            var repository = A.Fake<ITenantRepository>();
            A.CallTo(() => repository.GetSessionAsync()).Returns(A.Fake<IOctoSession>());
            A.CallTo(() => repository.GetRtEntitiesByTypeAsync<RtNotificationTemplate>(
                    A<IOctoSession>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .Returns(resultSet);

            var systemContext = A.Fake<ISystemContext>();
            A.CallTo(() => systemContext.FindTenantRepositoryAsync(A<string>._)).Returns(repository);

            var markdownRenderService = A.Fake<IMarkdownRenderService>();
            A.CallTo(() => markdownRenderService.RenderPlainText(A<string>._,
                    A<Dictionary<string, Func<string>>>._))
                .ReturnsLazily((string markdown, Dictionary<string, Func<string>> _) => "plain:" + markdown);
            A.CallTo(() => markdownRenderService.RenderHtml(A<string>._,
                    A<Dictionary<string, Func<string>>>._))
                .ReturnsLazily((string markdown, Dictionary<string, Func<string>> _) => "html:" + markdown);

            var distributionEventHubService = A.Fake<IDistributionEventHubService>();
            A.CallTo(() => distributionEventHubService.PublishAsync(A<SendNotificationsRequest>._,
                    A<CancellationToken?>._))
                .Invokes(call => _published = call.GetArgument<SendNotificationsRequest>(0))
                .Returns(Task.CompletedTask);

            Service = new NotificationService(systemContext, markdownRenderService, distributionEventHubService);
        }

        public INotificationService Service { get; }

        /// <summary>
        ///     The request handed to the distribution event hub. Fails rather than returns null when
        ///     nothing was published at all — "published nothing" is its own regression.
        /// </summary>
        public SendNotificationsRequest CapturedRequest()
        {
            Assert.NotNull(_published);
            return _published;
        }
    }
}
