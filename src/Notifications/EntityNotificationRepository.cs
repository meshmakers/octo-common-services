using Meshmakers.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.ConstructionKit.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Notifications.ConstructionKit.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Services.Notifications;

public class EntityNotificationRepository : INotificationRepository
{
    private readonly ISystemContext _systemContext;

    public EntityNotificationRepository(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    public async Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message)
    {
        await AddShortMessageAsync(tenantId, toPhoneNumber, message, null).ConfigureAwait(false);
    }

    public async Task AddEMailMessageAsync(string tenantId, string emailAddress, string subject,
        string? htmlMessage)
    {
        await AddEMailMessageAsync(tenantId, emailAddress, subject, htmlMessage, null).ConfigureAwait(false);
    }

    public async Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message,
        RtEntityId? associatedRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(toPhoneNumber), toPhoneNumber);
        ArgumentValidation.ValidateString(nameof(message), message);

        try
        {
            var notificationMessage = new NotificationMessageDto
            {
                SendStatus = SendStatusDto.Pending,
                BodyText = message,
                RecipientAddress = toPhoneNumber,
                NotificationType = NotificationTypesDto.Sms,
                LastTryDateTime = DateTime.MinValue
            };

            await AddMessageAsync(tenantId, notificationMessage, associatedRtId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new NotificationSendFailedException("Message send failed.", e);
        }
    }

    public async Task AddEMailMessageAsync(string tenantId, string emailAddress, string subject,
        string? htmlMessage, RtEntityId? associatedRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(emailAddress), emailAddress);

        try
        {
            var notificationMessage = new NotificationMessageDto
            {
                SendStatus = SendStatusDto.Pending,
                SubjectText = subject,
                BodyText = htmlMessage,
                RecipientAddress = emailAddress,
                NotificationType = NotificationTypesDto.EMail,
                LastTryDateTime = DateTime.MinValue
            };

            await AddMessageAsync(tenantId, notificationMessage, associatedRtId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new NotificationSendFailedException("Message send failed.", e);
        }
    }

    public async Task<PagedResult<NotificationMessageDto>> GetPendingMessagesAsync(string tenantId,
        NotificationTypesDto notificationType, int? take = null)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return new PagedResult<NotificationMessageDto>(new List<NotificationMessageDto>());
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtNotificationMessage>(session,
            DataQueryOperation.Create()
                .FieldFilter(nameof(RtNotificationMessage.SendStatus), FieldFilterOperator.Equals,
                    SendStatusDto.Pending)
                .FieldFilter(nameof(RtNotificationMessage.LastTryDateTime),
                    FieldFilterOperator.LessEqualThan, DateTime.UtcNow.AddMinutes(-5))
                .FieldFilter(nameof(RtNotificationMessage.NotificationType), FieldFilterOperator.Equals,
                    notificationType)
        ).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return new PagedResult<NotificationMessageDto>(result.Items.Select(CreateNotificationMessage), 0, take,
            result.TotalCount);
    }

    public async Task<IEnumerable<NotificationMessageDto>> StoreNotificationMessages(string tenantId,
        IEnumerable<NotificationMessageDto> notificationMessages)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            throw new NotificationSendFailedException("No system tenant is existing.");
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var entityUpdateInfos = new List<EntityUpdateInfo<RtNotificationMessage>>();
        foreach (var notificationMessageDto in notificationMessages)
        {
            var rtNotificationMessage = await PrepareUpdateRtEntityAsync(session, notificationMessageDto, tenantRepository)
                .ConfigureAwait(false);
            entityUpdateInfos.Add(EntityUpdateInfo<RtNotificationMessage>.CreateUpdate(rtNotificationMessage.ToRtEntityId(),
                rtNotificationMessage));
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, entityUpdateInfos, operationResult).ConfigureAwait(false);

        await session.CommitTransactionAsync().ConfigureAwait(false);

        return entityUpdateInfos.Select(x => CreateNotificationMessage(x.RtEntity!));
    }


    private async Task AddMessageAsync(string tenantId, NotificationMessageDto notificationMessageDto,
        RtEntityId? targetRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        if (!await _systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return;
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();

        var rtEntity = await CreateRtEntityAsync(notificationMessageDto, tenantRepository).ConfigureAwait(false);

        var associationUpdateInfos = new List<AssociationUpdateInfo>();
        if (targetRtId != null)
        {
            associationUpdateInfos.Add(new AssociationUpdateInfo(rtEntity.ToRtEntityId(), targetRtId.Value,
                SystemCkIds.Related, AssociationModOptionsDto.Create));
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, new[]
        {
            EntityUpdateInfo<RtNotificationMessage>.CreateInsert(rtEntity)
        }, associationUpdateInfos, operationResult).ConfigureAwait(false);


        await session.CommitTransactionAsync().ConfigureAwait(false);
    }

    private static async Task<RtNotificationMessage> CreateRtEntityAsync(NotificationMessageDto notificationMessageDto,
        ITenantRepository tenantContext)
    {
        var rtEntity = await tenantContext.CreateTransientRtEntityAsync<RtNotificationMessage>().ConfigureAwait(false);

        ApplyDtoData(notificationMessageDto, rtEntity);

        return rtEntity;
    }

    private static void ApplyDtoData(NotificationMessageDto notificationMessageDto,
        RtNotificationMessage rtEntity)
    {
        rtEntity.SubjectText = notificationMessageDto.SubjectText ?? string.Empty;
        rtEntity.BodyText = notificationMessageDto.BodyText;
        rtEntity.RecipientAddress = notificationMessageDto.RecipientAddress;
        rtEntity.SentDateTime = notificationMessageDto.SentDateTime;
        rtEntity.LastTryDateTime = notificationMessageDto.LastTryDateTime;
        rtEntity.ErrorText = notificationMessageDto.ErrorText;
        rtEntity.SendStatus = notificationMessageDto.SendStatus == null
            ? RtNotificationStatesEnum.Pending
            : (RtNotificationStatesEnum)notificationMessageDto.SendStatus;
        rtEntity.NotificationType = notificationMessageDto.NotificationType == null
            ? RtNotificationTypesEnum.EMail
            : (RtNotificationTypesEnum)notificationMessageDto.NotificationType;
    }

    private static async Task<RtNotificationMessage> PrepareUpdateRtEntityAsync(IOctoSession session,
        NotificationMessageDto notificationMessageDto,
        ITenantRepository tenantRepository)
    {
        var rtEntity =
            await tenantRepository.GetRtEntityByRtIdAsync<RtNotificationMessage>(session,
                notificationMessageDto.RtId).ConfigureAwait(false);
        if (rtEntity == null)
        {
            throw new NotificationSendFailedException($"Notification with id '{notificationMessageDto.RtId}' not found in repository.");
        }


        ApplyDtoData(notificationMessageDto, rtEntity);

        return rtEntity;
    }

    private static NotificationMessageDto CreateNotificationMessage(RtNotificationMessage rtEntity)
    {
        return new NotificationMessageDto
        {
            RtId = rtEntity.RtId,
            CkTypeId = rtEntity.CkTypeId,
            SubjectText = rtEntity.SubjectText,
            BodyText = rtEntity.BodyText,
            RecipientAddress = rtEntity.RecipientAddress,
            SentDateTime = rtEntity.SentDateTime,
            LastTryDateTime = rtEntity.LastTryDateTime,
            SendStatus = (SendStatusDto?)rtEntity.SendStatus,
            NotificationType = (NotificationTypesDto?)rtEntity.NotificationType,
            ErrorText = rtEntity.ErrorText
        };
    }
}