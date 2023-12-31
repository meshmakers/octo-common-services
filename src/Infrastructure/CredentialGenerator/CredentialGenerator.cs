using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Services.Infrastructure.CredentialGenerator;

/// <summary>
///     Helper class to generation Credentials
/// </summary>
public class CredentialGenerator : ICredentialGenerator
{
    private const int MinLength = 8;
    private const int MaxLength = 12;

    private const string SpecialCharsGenerator = "!@$?_-";
    private const string SpecialCharsUser = "!@$?_-!#$%&'()*+,-./:;<=>?@[]^_`{|}~";
    private const string Alphabetic = "ABCDEFGHJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";

    /// <summary>
    ///     Generate new username with next restrictions
    ///     min. 8 characters, max. 12 characters, (A-Z, a-z, 0-9, special characters (not in the first or last place))
    ///     (at least one character from every category must be included
    /// </summary>
    /// <param name="length">Username length</param>
    /// <returns></returns>
    public string NewUser(int? length = null)
    {
        return NewPassword(length);
    }

    /// <summary>
    ///     Generate new password with next restrictions
    ///     min. 8 characters, max. 12 characters, (A-Z, a-z, 0-9, special characters (not in the first or last place))
    ///     (at least one character from every category must be included
    /// </summary>
    /// <param name="length">Password length</param>
    /// <returns></returns>
    public string NewPassword(int? length = null)
    {
        var passwordLength = PasswordLength(length);

        // Requirement first and last Not Special characters
        var source = Alphabetic + Alphabetic.ToLower() + Digits;
        var first = GetRandomChar(source);
        var last = GetRandomChar(source);

        return first + GenerateValidMinimum(passwordLength - 2) + last;
    }

    /// <summary>
    ///     Check if a given password fulfills the password constraints
    /// </summary>
    /// <param name="password">Password to check</param>
    /// <returns></returns>
    public bool CheckPassword(string password)
    {
        if (password == null) throw new ArgumentNullException(nameof(password));

        if (password.Length < MinLength) return false;

        if (!(password.Any(char.IsUpper) &&
              password.Any(char.IsLower) &&
              password.Any(char.IsDigit)))
            return false;

        if (password.IndexOfAny(SpecialCharsUser.ToCharArray()) < 0) return false;

        return true;
    }

    private int PasswordLength(int? passwordLength)
    {
        if (passwordLength.HasValue)
        {
            if (passwordLength.Value < MinLength) return MinLength;

            if (passwordLength.Value >= MinLength && passwordLength.Value <= MaxLength) return passwordLength.Value;

            if (passwordLength.Value > MaxLength) return MaxLength;
        }

        // Get random password length
        return RandomGenerator.NextRandom(MinLength, MaxLength);
    }

    /// <summary>
    ///     Generate all chars, that will meet requirements and shuffle them
    /// </summary>
    /// <param name="arrayLength">Password length</param>
    /// <returns></returns>
    private string GenerateValidMinimum(int arrayLength)
    {
        var result = new char[arrayLength];

        // Generate letter by Restriction rules requirements
        result[0] = GetRandomChar(SpecialCharsGenerator);
        result[1] = GetRandomChar(Digits);
        result[2] = GetRandomChar(Alphabetic.ToUpper());
        result[3] = GetRandomChar(Alphabetic.ToLower());

        // Generate other letter randomly
        var source = Alphabetic.ToUpper() + Alphabetic.ToLower() + Digits + SpecialCharsGenerator;
        for (var i = 4; i < arrayLength; i++) result[i] = GetRandomChar(source);

        // Shuffle letters before return
        return new string(result.OrderBy(_ => RandomGenerator.NextRandom(0, source.Length - 1)).ToArray());
    }

    private char GetRandomChar(string source)
    {
        return source[RandomGenerator.NextRandom(0, source.Length - 1)];
    }
}