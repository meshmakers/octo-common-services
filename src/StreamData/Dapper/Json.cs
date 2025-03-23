
namespace Meshmakers.Octo.Services.StreamData.Dapper;


/// <summary>
/// Wrapper to enable dapper to handle JSON types
/// </summary>
/// <typeparam name="T"></typeparam>
internal class Json<T>
{
    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="value"></param>
    public Json(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>
    /// The value
    /// </summary>
    public T Value { get; }

    
    /// <summary>
    /// Convert to JSON
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static implicit operator Json<T>(T value) => new(value);
}