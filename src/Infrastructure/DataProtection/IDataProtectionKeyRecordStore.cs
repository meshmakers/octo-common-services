namespace Meshmakers.Octo.Services.Infrastructure.DataProtection;

/// <summary>A stored Data Protection key ring element.</summary>
/// <param name="StoredName">Unique stored name (<see cref="DataProtectionKeyNames" />)</param>
/// <param name="XmlData">Element XML as persisted (secret part wrapped when encryption is configured)</param>
internal sealed record DataProtectionKeyRecord(string StoredName, string XmlData);

/// <summary>
///     Persistence of Data Protection key ring elements, shared by every service of an installation. Each service
///     reads only its own elements (<see cref="DataProtectionKeyNames.BelongsTo" />).
/// </summary>
internal interface IDataProtectionKeyRecordStore
{
    /// <summary>
    ///     False while the store cannot be written yet (a fresh installation before the system tenant exists).
    /// </summary>
    Task<bool> CanPersistAsync();

    /// <summary>All stored elements, of every key ring.</summary>
    Task<IReadOnlyList<DataProtectionKeyRecord>> LoadAllAsync();

    /// <summary>
    ///     Stores <paramref name="record" />. A record with the same <see cref="DataProtectionKeyRecord.StoredName" />
    ///     that is already stored (another replica won the race) counts as stored.
    /// </summary>
    Task InsertAsync(DataProtectionKeyRecord record);
}
