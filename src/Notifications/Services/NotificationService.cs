using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Services.Notifications.Services;

internal class NotificationService(
    ISystemContext systemContext,
    IMarkdownRenderService markdownRenderService,
    IDistributionEventHubService distributionEventHubService) : INotificationService
{
    public async Task SendComplexAsync(string tenantId, string templateName, string recipient,
        Dictionary<string, Func<string>>? subjectVariables,
        Dictionary<string, Func<string>>? bodyVariables, string? cc, string? bcc)
    {
        var (subject, messageBody) =
            await RenderAsync(tenantId, templateName, subjectVariables, bodyVariables).ConfigureAwait(false);

        // TemplateName travels on this path too (AB#5223): identity's welcome, confirm and
        // reset-password mails all arrive here, and they are exactly the notifications an operator
        // looks for in the delivery log. Leaving it null would make every delivery record for the
        // platform's own mails miss the one field that says what was sent.
        //
        // ForcedChannel and CorrelationRtId stay null: the caller named a literal address rather
        // than a user, so there is no channel preference to override and no originating event to
        // correlate with. RecipientSubjectId stays unset for the same reason — see SendToUserAsync.
        var notification = new DistNotificationDto(subject, messageBody, recipient, cc, bcc)
        {
            TemplateName = templateName
        };

        await PublishAsync(tenantId, notification).ConfigureAwait(false);
    }

    public Task SendAsync(string tenantId, string templateName, string recipient,
        Dictionary<string, Func<string>>? variables = null,
        string? cc = null, string? bcc = null)
    {
        return SendComplexAsync(tenantId, templateName, recipient, variables, variables, cc, bcc);
    }

    public async Task SendToUserAsync(string tenantId, string subjectId, string templateName,
        Dictionary<string, Func<string>>? variables = null, string? forcedChannel = null,
        string? correlationRtId = null)
    {
        ArgumentValidation.ValidateString(nameof(subjectId), subjectId);

        var (subject, messageBody) =
            await RenderAsync(tenantId, templateName, variables, variables).ConfigureAwait(false);

        // Recipient stays null on purpose: the address is not known here and must not be guessed.
        // ResolveNotificationChannel@1 fills in the delivery target at send time — the user's
        // preferred binding when there is one, the directory e-mail otherwise.
        var notification = new DistNotificationDto(subject, messageBody, null, null, null)
        {
            RecipientSubjectId = subjectId,
            ForcedChannel = forcedChannel,
            TemplateName = templateName,
            CorrelationRtId = correlationRtId
        };

        await PublishAsync(tenantId, notification).ConfigureAwait(false);
    }

    private async Task<(string Subject, string? Body)> RenderAsync(string tenantId, string templateName,
        Dictionary<string, Func<string>>? subjectVariables, Dictionary<string, Func<string>>? bodyVariables)
    {
        var template = await GetNotificationTemplateAsync(tenantId, templateName).ConfigureAwait(false);
        var skipRendering = ShouldSkipRendering(template);

        string? messageBody = null;
        if (!string.IsNullOrWhiteSpace(template.BodyTemplate))
        {
            messageBody = skipRendering
                ? markdownRenderService.RenderPlainText(template.BodyTemplate,
                    bodyVariables ?? new Dictionary<string, Func<string>>())
                : markdownRenderService.RenderHtml(template.BodyTemplate,
                    bodyVariables ?? new Dictionary<string, Func<string>>());
        }

        var subject = markdownRenderService.RenderPlainText(template.SubjectTemplate,
            subjectVariables ?? new Dictionary<string, Func<string>>());

        return (subject, messageBody);
    }

    private Task PublishAsync(string tenantId, DistNotificationDto notification)
    {
        var request = new SendNotificationsRequest(tenantId);
        request.Notifications.Add(notification);

        return distributionEventHubService.PublishAsync(request);
    }

    private async Task<RtNotificationTemplate> GetNotificationTemplateAsync(string tenantId, string templateName)
    {
        var repository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        using var session = await repository.GetSessionAsync().ConfigureAwait(false);

        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.Equals, templateName);

        var result = await repository.GetRtEntitiesByTypeAsync<RtNotificationTemplate>(session, queryOptions)
            .ConfigureAwait(false);

        if (result.TotalCount == 0)
        {
            throw NotificationException.TemplateNotFound(templateName);
        }

        if (result.TotalCount != 1)
        {
            throw NotificationException.TemplateAmbiguous(templateName);
        }

        var templateEntity = result.Items.Single();

        ValidateTemplate(templateEntity);

        return templateEntity;
    }

    private void ValidateTemplate(RtNotificationTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.SubjectTemplate) || string.IsNullOrWhiteSpace(template.BodyTemplate))
        {
            throw NotificationException.TemplateInvalid(template.RtWellKnownName!);
        }
    }

    private bool ShouldSkipRendering(RtNotificationTemplate template)
    {
        return template.RenderingType == RtRenderingTypesEnum.Plain;
    }
}