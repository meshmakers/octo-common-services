using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Services.Notifications;

public class EventRepository(ISystemContext systemContext) : IEventRepository
{
    private async Task AddMessageAsync(string tenantId, RtEvent rtEvent,
        RtEntityId? targetRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
    
        if (!await systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return;
        }
    
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
    
        var associationUpdateInfos = new List<AssociationUpdateInfo>();
        if (targetRtId != null)
        {
            associationUpdateInfos.Add(new AssociationUpdateInfo(rtEvent.ToRtEntityId(), targetRtId.Value,
                SystemCkIds.Related, AssociationModOptionsDto.Create));
        }
    
        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, [
            EntityUpdateInfo<RtEvent>.CreateInsert(rtEvent)
        ], associationUpdateInfos, operationResult).ConfigureAwait(false);
    
    
        await session.CommitTransactionAsync().ConfigureAwait(false);
    }

    public async Task StoreEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string message, 
        RtEntityId? associatedRtId = null)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(message), message);
        
        try
        {
            var rtEvent = new RtEvent
            {
                Message = message,
                Level = eventLevel
            };
        
            await AddMessageAsync(tenantId, rtEvent, associatedRtId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }

    public async Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtId = null)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(message), message);
        
        try
        {
            var rtStatefulEvent = new RtStatefulEvent
            {
                RtId = OctoObjectId.GenerateNewId(),
                Message = message,
                State = RtEventStatesEnum.Active,
                Level = eventLevel
            };
        
            await AddMessageAsync(tenantId, rtStatefulEvent, associatedRtId).ConfigureAwait(false);

            return rtStatefulEvent;
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }
}