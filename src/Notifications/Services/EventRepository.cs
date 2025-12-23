using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;

namespace Meshmakers.Octo.Services.Notifications.Services;

public class EventRepository(ISystemContext systemContext) : IEventRepository
{
    private async Task AddMessageAsync(RtEvent rtEvent, string? tenantId,
        RtEntityId? associatedRtEntityId)
    {
        if (!await systemContext.IsSystemTenantExistingAsync().ConfigureAwait(false))
        {
            return;
        }

        var tenantRepository = systemContext.GetTenantRepository();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId).ConfigureAwait(false);
        }
        using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
    
        var associationUpdateInfos = new List<AssociationUpdateInfo>();
        if (associatedRtEntityId != null)
        {
            associationUpdateInfos.Add(new AssociationUpdateInfo(rtEvent.ToRtEntityId(), associatedRtEntityId.Value,
                SystemCkIds.RtCkRelatedRoleId, AssociationModOptionsDto.Create));
        }
    
        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session, [
            EntityUpdateInfo<RtEvent>.CreateInsert(rtEvent)
        ], associationUpdateInfos, operationResult).ConfigureAwait(false);
    
    
        await session.CommitTransactionAsync().ConfigureAwait(false);
    }

    public async Task StoreEventAsync(string tenantId, RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message, 
        RtEntityId? associatedRtEntityId = null)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(message), message);
        
        try
        {
            var rtEvent = new RtEvent
            {
                Message = message,
                Source = source,
                Level = eventLevel
            };
        
            await AddMessageAsync(rtEvent, tenantId, associatedRtEntityId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }
    
    public async Task StoreSystemEventAsync(RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message, 
        RtEntityId? associatedRtEntityId = null)
    {
        ArgumentValidation.ValidateString(nameof(message), message);
        
        try
        {
            var rtEvent = new RtEvent
            {
                Message = message,
                Source = source,
                Level = eventLevel
            };
        
            await AddMessageAsync(rtEvent, null, associatedRtEntityId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }

    public async Task<RtStatefulEvent> StoreStatefulEventAsync(string tenantId, RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtEntityId = null)
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
                Source = source,
                Level = eventLevel
            };
        
            await AddMessageAsync(rtStatefulEvent, tenantId, associatedRtEntityId).ConfigureAwait(false);

            return rtStatefulEvent;
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }
    
    public async Task<RtStatefulEvent> StoreSystemStatefulEventAsync(RtEventSourcesEnum source, RtEventLevelsEnum eventLevel, string message,
        RtEntityId? associatedRtEntityId = null)
    {
        ArgumentValidation.ValidateString(nameof(message), message);
        
        try
        {
            var rtStatefulEvent = new RtStatefulEvent
            {
                RtId = OctoObjectId.GenerateNewId(),
                Message = message,
                State = RtEventStatesEnum.Active,
                Source = source,
                Level = eventLevel
            };
        
            await AddMessageAsync(rtStatefulEvent, null, associatedRtEntityId).ConfigureAwait(false);

            return rtStatefulEvent;
        }
        catch (Exception e)
        {
            throw new EventStoreFailedException("Event store failed.", e);
        }
    }

    public Task StoreInformationEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreEventAsync(tenantId, source, RtEventLevelsEnum.Information, message, associatedRtEntityId);
    }

    public Task StoreWarningEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreEventAsync(tenantId, source, RtEventLevelsEnum.Warning, message, associatedRtEntityId);
    }

    public Task StoreErrorEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreEventAsync(tenantId, source, RtEventLevelsEnum.Error, message, associatedRtEntityId);
    }

    public Task StoreCriticalEvent(string tenantId, RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreEventAsync(tenantId, source, RtEventLevelsEnum.Critical, message, associatedRtEntityId);
    }
    
    public Task StoreSystemInformationEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreSystemEventAsync(source, RtEventLevelsEnum.Information, message, associatedRtEntityId);
    }

    public Task StoreSystemWarningEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreSystemEventAsync(source, RtEventLevelsEnum.Warning, message, associatedRtEntityId);
    }

    public Task StoreSystemErrorEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreSystemEventAsync(source, RtEventLevelsEnum.Error, message, associatedRtEntityId);
    }

    public Task StoreSystemCriticalEvent(RtEventSourcesEnum source, string message, RtEntityId? associatedRtEntityId = null)
    {
        return StoreSystemEventAsync(source, RtEventLevelsEnum.Critical, message, associatedRtEntityId);
    }
}