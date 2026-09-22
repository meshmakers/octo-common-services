using System.Text.Json;
using MassTransit.Serialization;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Xunit;

namespace Notifications.Tests;

/// <summary>
///     Pins the System.Text.Json wire contract of <see cref="SendNotificationsRequest" /> and
///     <see cref="DistNotificationDto" /> — the message identity publishes on the distribution event
///     hub and <c>FromSendNotification@1</c> consumes.
/// </summary>
/// <remarks>
///     <para>
///         The options under test are the ones that actually travel. octo-distributedEventHub
///         registers no custom serializer and no custom <see cref="JsonSerializerOptions" />
///         (<c>src/DistributionEventHub/Configuration/DependencyInjection/ServiceCollectionExtensions.cs</c>
///         configures only scheduler, retry, topology and endpoint naming), so MassTransit's default
///         applies: <see cref="SystemTextJsonMessageSerializer.Options" />. Referencing that field
///         rather than re-declaring an equivalent means the test follows MassTransit if its defaults
///         ever move.
///     </para>
///     <para>
///         The two hand-rolled flavours are kept alongside it so the contract also holds for a
///         producer or consumer that serializes these types outside the bus (a pipeline node, a
///         test harness, a replay tool) under either plausible convention.
///     </para>
/// </remarks>
public class NotificationWireContractTests
{
    private const string TenantId = "test-tenant";
    private const string Subject = "subject-value";
    private const string Body = "body-value";
    private const string Recipient = "recipient@example.com";
    private const string Cc = "cc@example.com";
    private const string Bcc = "bcc@example.com";
    private const string RecipientSubjectId = "6501f2a3b4c5d6e7f8a9b0c1";
    private const string ForcedChannel = "SIGNAL";
    private const string TemplateName = "System.Identity.OneTimePassword";
    private const string CorrelationRtId = "6501f2a3b4c5d6e7f8a9b0c2";

    private static readonly DateTime SendAt = new(2026, 9, 14, 10, 30, 45, DateTimeKind.Utc);

    /// <summary>
    ///     The serializer configurations the contract has to hold under. Passed as an enum rather than
    ///     as a <see cref="JsonSerializerOptions" /> instance so xUnit can serialize the theory data.
    /// </summary>
    public enum SerializerFlavour
    {
        /// <summary>MassTransit's own options — what really travels on the hub.</summary>
        DistributionEventHub,

        /// <summary>Stock options: PascalCase names, case-sensitive matching.</summary>
        Default,

        /// <summary>camelCase names with case-insensitive matching.</summary>
        CamelCaseInsensitive
    }

    private static JsonSerializerOptions OptionsFor(SerializerFlavour flavour)
    {
        return flavour switch
        {
            SerializerFlavour.DistributionEventHub => SystemTextJsonMessageSerializer.Options,
            SerializerFlavour.Default => new JsonSerializerOptions(),
            SerializerFlavour.CamelCaseInsensitive => new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(flavour), flavour, null)
        };
    }

    private static SendNotificationsRequest CreateFullyPopulatedRequest()
    {
        var request = new SendNotificationsRequest(TenantId) { SendAt = SendAt };
        request.Notifications.Add(new DistNotificationDto(Subject, Body, Recipient, Cc, Bcc)
        {
            RecipientSubjectId = RecipientSubjectId,
            ForcedChannel = ForcedChannel,
            TemplateName = TemplateName,
            CorrelationRtId = CorrelationRtId
        });

        return request;
    }

    private static SendNotificationsRequest RoundTrip(SendNotificationsRequest request, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(request, options);
        var result = JsonSerializer.Deserialize<SendNotificationsRequest>(json, options);

        Assert.NotNull(result);
        return result;
    }

    /// <summary>
    ///     Documents how the hand-written JSON below is allowed to use PascalCase keys: the options
    ///     that travel match property names case-insensitively. If MassTransit ever drops that, the
    ///     backwards- and forward-compatibility payloads in this file stop being representative and
    ///     have to be rewritten rather than deleted.
    /// </summary>
    [Fact]
    public void DistributionEventHubOptions_MatchPropertyNamesCaseInsensitively()
    {
        var options = SystemTextJsonMessageSerializer.Options;

        Assert.True(options.PropertyNameCaseInsensitive);
        Assert.Equal("recipientSubjectId", options.PropertyNamingPolicy?.ConvertName("RecipientSubjectId"));
    }

    [Theory]
    [InlineData(SerializerFlavour.DistributionEventHub)]
    [InlineData(SerializerFlavour.Default)]
    [InlineData(SerializerFlavour.CamelCaseInsensitive)]
    public void FullyPopulatedRequest_PreservesEveryValueAcrossTheWire(SerializerFlavour flavour)
    {
        var result = RoundTrip(CreateFullyPopulatedRequest(), OptionsFor(flavour));

        Assert.Equal(TenantId, result.TenantId);
        Assert.NotEmpty(result.Notifications);

        var notification = Assert.Single(result.Notifications);

        // Asserted value by value on purpose. A single Assert.Equal on the record would compare an
        // empty collection against an empty collection just as happily as a populated one.
        Assert.Equal(Subject, notification.Subject);
        Assert.Equal(Body, notification.Body);
        Assert.Equal(Recipient, notification.Recipient);
        Assert.Equal(Cc, notification.Cc);
        Assert.Equal(Bcc, notification.Bcc);
        Assert.Equal(RecipientSubjectId, notification.RecipientSubjectId);
        Assert.Equal(ForcedChannel, notification.ForcedChannel);
        Assert.Equal(TemplateName, notification.TemplateName);
        Assert.Equal(CorrelationRtId, notification.CorrelationRtId);
    }

    /// <summary>
    ///     AB#5136 regression guard — do not delete, and do not weaken to "not null".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What regressed: <see cref="SendNotificationsRequest.Notifications" /> was a
    ///         <b>get-only</b> collection initialised in the parameterized constructor. System.Text.Json
    ///         — the serializer MassTransit uses on the distribution event hub — deserializes such a type
    ///         through its constructor and then only assigns members it can write. With no setter it
    ///         never populated the list, so the consumer received a well-formed
    ///         <c>SendNotificationsRequest</c> with an <b>empty</b> collection.
    ///     </para>
    ///     <para>
    ///         What it looked like: identity's OTP, welcome and reset-password mails were published
    ///         successfully and never delivered. Nothing threw, nothing was logged, no message faulted —
    ///         <c>FromSendNotification@1</c> simply had nothing to iterate. The single visible symptom was
    ///         a pipeline execution that "completed" in under a millisecond.
    ///     </para>
    ///     <para>
    ///         Which is why the assertion is on the element count: any member of this contract that
    ///         System.Text.Json cannot write fails silently and looks like success.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(SerializerFlavour.DistributionEventHub)]
    [InlineData(SerializerFlavour.Default)]
    [InlineData(SerializerFlavour.CamelCaseInsensitive)]
    public void Ab5136_NotificationsCollectionMustNotArriveEmptyAtTheConsumer(SerializerFlavour flavour)
    {
        var result = RoundTrip(CreateFullyPopulatedRequest(), OptionsFor(flavour));

        // Assert.Single is "exactly one element" — it fails on an empty collection (the regression)
        // just as it fails on a duplicated one. Analyzer xUnit2013 rules out the equivalent
        // Assert.Equal(1, ....Count).
        Assert.Single(result.Notifications);
    }

    [Theory]
    [InlineData(SerializerFlavour.DistributionEventHub)]
    [InlineData(SerializerFlavour.Default)]
    [InlineData(SerializerFlavour.CamelCaseInsensitive)]
    public void SendAt_SurvivesTheRoundTrip(SerializerFlavour flavour)
    {
        var result = RoundTrip(CreateFullyPopulatedRequest(), OptionsFor(flavour));

        Assert.Equal(SendAt, result.SendAt);
    }

    /// <summary>
    ///     Backwards compatibility: a producer that predates AB#5217 emits no
    ///     <c>RecipientSubjectId</c> / <c>ForcedChannel</c> / <c>TemplateName</c> / <c>CorrelationRtId</c>
    ///     key at all. The payload is written out by hand precisely because serializing the current type
    ///     could never produce an old producer's shape.
    /// </summary>
    [Theory]
    [InlineData(SerializerFlavour.DistributionEventHub)]
    [InlineData(SerializerFlavour.Default)]
    [InlineData(SerializerFlavour.CamelCaseInsensitive)]
    public void PayloadFromProducerWithoutTheAb5217Members_DeserializesWithThoseMembersNull(SerializerFlavour flavour)
    {
        const string json = """
                            {
                              "TenantId": "test-tenant",
                              "SendAt": "2026-09-14T10:30:45Z",
                              "Notifications": [
                                {
                                  "Subject": "subject-value",
                                  "Body": "body-value",
                                  "Recipient": "recipient@example.com",
                                  "Cc": "cc@example.com",
                                  "Bcc": "bcc@example.com"
                                }
                              ]
                            }
                            """;

        var result = JsonSerializer.Deserialize<SendNotificationsRequest>(json, OptionsFor(flavour));

        Assert.NotNull(result);
        Assert.Equal(TenantId, result.TenantId);
        Assert.Equal(SendAt, result.SendAt);

        var notification = Assert.Single(result.Notifications);

        Assert.Equal(Subject, notification.Subject);
        Assert.Equal(Body, notification.Body);
        Assert.Equal(Recipient, notification.Recipient);
        Assert.Equal(Cc, notification.Cc);
        Assert.Equal(Bcc, notification.Bcc);

        Assert.Null(notification.RecipientSubjectId);
        Assert.Null(notification.ForcedChannel);
        Assert.Null(notification.TemplateName);
        Assert.Null(notification.CorrelationRtId);
    }

    /// <summary>
    ///     Forward compatibility: a newer producer that has added a member to either type must not fault
    ///     the message at an older consumer. Unknown keys are skipped, not rejected.
    /// </summary>
    [Theory]
    [InlineData(SerializerFlavour.DistributionEventHub)]
    [InlineData(SerializerFlavour.Default)]
    [InlineData(SerializerFlavour.CamelCaseInsensitive)]
    public void PayloadFromNewerProducerWithUnknownMembers_DeserializesWithoutThrowing(SerializerFlavour flavour)
    {
        const string json = """
                            {
                              "TenantId": "test-tenant",
                              "SendAt": "2026-09-14T10:30:45Z",
                              "SomeFutureRequestMember": 42,
                              "Notifications": [
                                {
                                  "Subject": "subject-value",
                                  "Body": "body-value",
                                  "Recipient": "recipient@example.com",
                                  "Cc": "cc@example.com",
                                  "Bcc": "bcc@example.com",
                                  "RecipientSubjectId": "6501f2a3b4c5d6e7f8a9b0c1",
                                  "SomeFutureNotificationMember": { "nested": true }
                                }
                              ]
                            }
                            """;

        var result = JsonSerializer.Deserialize<SendNotificationsRequest>(json, OptionsFor(flavour));

        Assert.NotNull(result);

        var notification = Assert.Single(result.Notifications);

        Assert.Equal(Subject, notification.Subject);
        Assert.Equal(RecipientSubjectId, notification.RecipientSubjectId);
    }
}
