namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>
///     The closed list of CK methods that an administrator of a <b>parent</b> tenant may call on a child tenant: the
///     method counterparts of the actions of the bot service's <c>JobsController</c>, which carries
///     <c>[AllowParentTenantAdministration]</c> on the REST side. The opt-in lives here, at the method, and never on an
///     endpoint as a whole: a parent administrator <b>administers</b> a child tenant (backup, restore, archive export and import),
///     it does not read or write the child's data.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Closed on purpose.</b> Every id here widens who may call the method: a user token issued for tenant P may call
///         it on tenant C when C is a registered child of P (<c>ITenantHierarchyReader.IsChildTenantAsync</c>, one level for an
///         ordinary parent, the whole estate for the system tenant). Any further method needs its own story with a security
///         review; never add an id that reads or writes tenant content.
///     </para>
///     <para>
///         The ids are the qualified, version-less method ids of the call (<c>CkMethodRequest.MethodId</c>), compared ordinally
///         (case-sensitive): a new version of a method (<c>Dump-2</c>) is a new id and not on the list.
///     </para>
///     <para>
///         The roles stay those the method declares (<c>authorization.roles: [TenantManagement]</c>, the equivalent of the REST
///         policy <c>TenantJobManagementPolicy</c>); they are judged on the caller's token as for every call. Service tokens
///         (client credentials, no <c>sub</c>) never use this list: they keep the exact <c>tenant_id</c> match plus
///         <c>TenantAuthorization:CrossTenantServiceClientIds</c>.
///     </para>
/// </remarks>
public static class CkParentTenantAdministrationMethods
{
    /// <summary><c>System.Bot/TenantRepository.Dump-1</c>: backup of the tenant repository (REST: dump job).</summary>
    public const string TenantRepositoryDump = "System.Bot/TenantRepository.Dump-1";

    /// <summary><c>System.Bot/TenantRepository.Restore-1</c>: restore of the tenant repository (REST: restore job).</summary>
    public const string TenantRepositoryRestore = "System.Bot/TenantRepository.Restore-1";

    /// <summary><c>System.Bot/TenantRepository.ExportArchiveData-1</c>: export of the archive data (REST: export job).</summary>
    public const string TenantRepositoryExportArchiveData = "System.Bot/TenantRepository.ExportArchiveData-1";

    /// <summary><c>System.Bot/TenantRepository.ImportArchiveData-1</c>: import of archive data (REST: import job).</summary>
    public const string TenantRepositoryImportArchiveData = "System.Bot/TenantRepository.ImportArchiveData-1";

    /// <summary>
    ///     The tenant role that the REST counterpart requires (<c>TenantJobManagementPolicy</c>) and every method of the list
    ///     declares. Readers of the operation records of these methods on the parent path (<c>runtime.operations</c>) require it
    ///     too.
    /// </summary>
    public const string RequiredRole = "TenantManagement";

    /// <summary>The closed list.</summary>
    public static IReadOnlyCollection<string> MethodIds { get; } =
    [
        TenantRepositoryDump,
        TenantRepositoryRestore,
        TenantRepositoryExportArchiveData,
        TenantRepositoryImportArchiveData
    ];

    /// <summary><c>true</c> when <paramref name="methodId" /> is on the closed list (ordinal comparison, no trimming).</summary>
    public static bool Contains(string? methodId) =>
        methodId is not null && MethodIds.Contains(methodId, StringComparer.Ordinal);
}
