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

        var notification = new DistNotificationDto(subject, messageBody, recipient, cc, bcc);

        var request = new SendNotificationsRequest(tenantId);
        request.Notifications.Add(notification);

        await distributionEventHubService.PublishAsync(request).ConfigureAwait(false);
    }

    public Task SendAsync(string tenantId, string templateName, string recipient,
        Dictionary<string, Func<string>>? variables = null,
        string? cc = null, string? bcc = null)
    {
        return SendComplexAsync(tenantId, templateName, recipient, variables, variables, cc, bcc);
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