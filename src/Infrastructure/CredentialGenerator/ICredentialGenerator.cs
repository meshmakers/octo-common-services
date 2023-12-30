namespace Meshmakers.Octo.Backend.Infrastructure.CredentialGenerator;

public interface ICredentialGenerator
{
    /// <summary>
    ///     Generate new username with next restrictions
    ///     min. 8 characters, max. 12 characters, (A-Z, a-z, 0-9, special characters (not in the first or last place))
    ///     (at least one character from every category must be included
    /// </summary>
    /// <param name="length">Username length</param>
    /// <returns></returns>
    string NewUser(int? length = null);

    /// <summary>
    ///     Generate new password with next restrictions
    ///     min. 8 characters, max. 12 characters, (A-Z, a-z, 0-9, special characters (not in the first or last place))
    ///     (at least one character from every category must be included
    /// </summary>
    /// <param name="length">Password length</param>
    /// <returns></returns>
    string NewPassword(int? length = null);

    /// <summary>
    ///     Check if a given password fulfills the password constraints
    /// </summary>
    /// <param name="password">Password to check</param>
    /// <returns></returns>
    bool CheckPassword(string password);
}