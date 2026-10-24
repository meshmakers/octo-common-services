using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>Everything the host knows about one registered method handler.</summary>
internal sealed class CkMethodRegistration
{
    private CkMethodRegistration(string methodId, Type handlerType, Type parametersType, Type? resultType,
        CkMethodHandlerAdapter adapter)
    {
        Adapter = adapter;
        MethodId = methodId;
        HandlerType = handlerType;
        ParametersType = parametersType;
        ResultType = resultType;
    }

    public string MethodId { get; }

    public CkMethodHandlerAdapter Adapter { get; }

    public Type HandlerType { get; }

    public Type ParametersType { get; }

    /// <summary>Null for a handler of a method without a result.</summary>
    public Type? ResultType { get; }

    /// <summary>Set by the startup validation (null when validation is off).</summary>
    public CkMethodDto? Definition { get; set; }

    /// <summary>Names (as in the CK model) of the parameters that carry <c>sensitive: true</c>.</summary>
    public IReadOnlySet<string> SensitiveParameters { get; set; } = new HashSet<string>();

    /// <summary>
    ///     <c>true</c> when the method's result carries <c>sensitive: true</c> (AB#7007). Fail-closed (<c>true</c>) until
    ///     the startup validation has read the definition, so a registration that is not validated logs no result detail.
    /// </summary>
    public bool SensitiveResult { get; set; } = true;

    public static CkMethodRegistration Create(string methodId, Type handlerType)
    {
        // validates the id early (throws ArgumentException for malformed ids)
        CkMethodQueueNames.For(methodId);

        var typed = handlerType.GetInterfaces()
            .Where(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICkMethodHandler<>) ||
                                            i.GetGenericTypeDefinition() == typeof(ICkMethodHandler<,>)))
            .ToList();
        if (typed.Count != 1)
        {
            throw new InvalidOperationException(
                $"Handler {handlerType.Name} for CK method '{methodId}' must implement exactly one of " +
                $"ICkMethodHandler<TParameters> or ICkMethodHandler<TParameters, TResult> (found {typed.Count}).");
        }

        var arguments = typed[0].GetGenericArguments();
        var adapterType = arguments.Length > 1
            ? typeof(CkMethodHandlerAdapter<,,>).MakeGenericType(handlerType, arguments[0], arguments[1])
            : typeof(CkMethodHandlerAdapter<,>).MakeGenericType(handlerType, arguments[0]);
        return new CkMethodRegistration(methodId, handlerType, arguments[0], arguments.Length > 1 ? arguments[1] : null,
            (CkMethodHandlerAdapter)Activator.CreateInstance(adapterType)!);
    }

    /// <summary>Parameters type, deserialized from the wire; a bad payload becomes a validation failure.</summary>
    public object DeserializeParameters(string parametersJson)
    {
        var json = string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson;
        try
        {
            return JsonSerializer.Deserialize(json, ParametersType, CkMethodJson.Options)
                   ?? throw new CkMethodParameterException("$", "null-parameters");
        }
        catch (JsonException e)
        {
            // Only the member path and the names of missing required members are used: the exception message of
            // System.Text.Json and the payload never reach the wire or a log.
            throw new CkMethodParameterException(FieldOf(e, json), "invalid-value");
        }
    }

    private string FieldOf(JsonException e, string json)
    {
        var path = e.Path;
        if (!string.IsNullOrEmpty(path) && path != "$")
        {
            return path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
        }

        return FirstMissingRequiredMember(json) ?? "$";
    }

    private string? FirstMissingRequiredMember(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var present = document.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var typeInfo = CkMethodJson.Options.GetTypeInfo(ParametersType);
            return typeInfo.Properties.FirstOrDefault(p => p.IsRequired && !present.Contains(p.Name))?.Name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

}

/// <summary>A payload that does not fit the parameter record. Carries the field name only, never a value.</summary>
internal sealed class CkMethodParameterException(string field, string reason) : Exception("Invalid method parameters")
{
    public string Field { get; } = field;

    public string Reason { get; } = reason;
}
