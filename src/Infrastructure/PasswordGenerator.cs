using System.Security.Cryptography;
using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Services.Infrastructure;

/// <summary>
///     Generates passwords with different options
/// </summary>
public class PasswordGenerator
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    private static readonly object Locker = new();

    private const string UppercaseCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string LowercaseCharacters = "abcdefghijklmnopqrstuvwxyz";
    private const string DigitCharacters = "0123456789";
    private const string SpecialCharacters = "!§$%&/([]}{@µ|-_:*+~'°,)=?`";

    /// <summary>
    ///     Generates a random string containing letters, numbers and symbols.
    ///     Guarantees at least one character from each category (uppercase, lowercase, digit, special).
    /// </summary>
    /// <param name="length"></param>
    /// <returns></returns>
    public static string GetRandomAlphanumericString(int length)
    {
        const string allCharacters =
            UppercaseCharacters +
            LowercaseCharacters +
            DigitCharacters +
            SpecialCharacters;

        var result = GetRandomString(length, allCharacters);

        // Ensure at least one character from each required category is present.
        // Replace characters at random positions if a category is missing.
        var resultChars = result.ToCharArray();
        var usedPositions = new HashSet<int>();

        EnsureCategory(resultChars, UppercaseCharacters, usedPositions, length);
        EnsureCategory(resultChars, LowercaseCharacters, usedPositions, length);
        EnsureCategory(resultChars, DigitCharacters, usedPositions, length);
        EnsureCategory(resultChars, SpecialCharacters, usedPositions, length);

        return new string(resultChars);
    }

    /// <summary>
    ///     Generates a random string
    /// </summary>
    /// <param name="length">Length of string</param>
    /// <param name="characterSet">The us</param>
    /// <returns>The generated string</returns>
    /// <exception cref="ArgumentException">Exception </exception>
    public static string GetRandomString(int length, IEnumerable<char> characterSet)
    {
        ArgumentValidation.ValidateInt(nameof(length), length, 0);

        if (length > int.MaxValue / 8) // 250 million chars ought to be enough for anybody
        {
            throw new ArgumentException(@"length is too big", nameof(length));
        }

        var characterArray = characterSet.Distinct().ToArray();
        if (characterArray.Length == 0)
        {
            throw new ArgumentException(@"characterSet must not be empty", nameof(characterSet));
        }

        var bytes = new byte[length * 8];
        lock (Locker)
        {
            Rng.GetBytes(bytes);
        }

        var result = new char[length];
        for (var i = 0; i < length; i++)
        {
            var value = BitConverter.ToUInt64(bytes, i * 8);
            result[i] = characterArray[value % (uint)characterArray.Length];
        }

        return new string(result);
    }

    private static void EnsureCategory(char[] resultChars, string category, HashSet<int> usedPositions, int length)
    {
        if (resultChars.Any(c => category.Contains(c)))
        {
            return;
        }

        // Pick a random position that hasn't been used for another category fix
        int position;
        var positionBytes = new byte[4];
        do
        {
            lock (Locker)
            {
                Rng.GetBytes(positionBytes);
            }

            position = (int)(BitConverter.ToUInt32(positionBytes, 0) % (uint)length);
        } while (!usedPositions.Add(position));

        // Pick a random character from the required category
        var charBytes = new byte[4];
        lock (Locker)
        {
            Rng.GetBytes(charBytes);
        }

        resultChars[position] = category[(int)(BitConverter.ToUInt32(charBytes, 0) % (uint)category.Length)];
    }
}