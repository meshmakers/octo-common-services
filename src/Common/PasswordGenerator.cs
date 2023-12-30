using System.Security.Cryptography;
using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Services.Common;

/// <summary>
///     Generates passwords with different options
/// </summary>
public class PasswordGenerator
{
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
    private static readonly object Locker = new();

    /// <summary>
    ///     Generates a random string containing letters, numbers and symbols.
    /// </summary>
    /// <param name="length"></param>
    /// <returns></returns>
    public static string GetRandomAlphanumericString(int length)
    {
        const string alphanumericCharacters =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
            "abcdefghijklmnopqrstuvwxyz" +
            "0123456789" +
            "!§$%&/([]}{@µ|-_:*+~'°,)=?`";
        return GetRandomString(length, alphanumericCharacters);
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
            throw new ArgumentException(@"length is too big", nameof(length));

        var characterArray = characterSet.Distinct().ToArray();
        if (characterArray.Length == 0) throw new ArgumentException(@"characterSet must not be empty", nameof(characterSet));

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
}