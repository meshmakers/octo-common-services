using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Services.Methods;

/// <summary>
///     The JSON options for method parameters and results on the wire (<c>parametersJson</c>, <c>resultJson</c>):
///     camelCase, case-insensitive reading, enums written as names (numbers are still accepted when reading).
///     Every transport (bus, in-process, pipeline) must use these options so that all of them produce and read the same JSON.
/// </summary>
public static class CkMethodJson
{
    /// <summary>The shared serializer options. Do not modify.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
