namespace Meshmakers.Octo.Services.Methods.Authorization;

/// <summary>Options of the method authorization (configuration section <c>Methods:Authorization</c>).</summary>
public sealed class CkMethodAuthorizationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = CkMethodHandlerOptions.SectionName + ":Authorization";

    /// <summary>The scope every method call requires, in addition to the scopes the method declares.</summary>
    public const string ApiScope = "octo_api";

    /// <summary>The administrator roles used when <see cref="AdministratorRoles" /> is empty.</summary>
    public static readonly IReadOnlyList<string> DefaultAdministratorRoles = ["AdminPanelManagement"];

    /// <summary>
    ///     Roles of an administrator: the only callers of a method that declares no <c>authorization</c> or no roles
    ///     (default-deny). Any one of them suffices. Empty (the default) means <see cref="DefaultAdministratorRoles" />, the
    ///     tenant administrator role <c>AdminPanelManagement</c>; configured roles <b>replace</b> the default (the list
    ///     starts empty so that configuration binding does not append to it).
    /// </summary>
    public IList<string> AdministratorRoles { get; set; } = [];
}
