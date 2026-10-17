using System.Reflection;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Services.Methods.Handlers;

/// <summary>
///     Checks every handler registration against the CK definition of its method and records what the host needs at
///     run time (sensitive parameters, declared error codes). A mismatch is a programming error: it fails the startup of
///     the service with a message that names the method, instead of failing the first call in production.
/// </summary>
internal sealed class CkMethodRegistrationValidator(ICkMethodDefinitionSource definitions)
{
    public void Validate(CkMethodHandlerRegistrations registrations)
    {
        foreach (var registration in registrations.Handlers.Values.OrderBy(r => r.MethodId, StringComparer.Ordinal))
        {
            Validate(registration);
        }
    }

    private void Validate(CkMethodRegistration registration)
    {
        var method = definitions.Find(registration.MethodId) ?? throw Invalid(registration,
            $"the CK cache of tenant(s) {string.Join(", ", definitions.TenantIds.Select(t => $"'{t}'"))} has no " +
            "such method. Check the method id (use the generated constant of the model's *CkIds class, without the " +
            "model version) and that the model is installed in that tenant");

        CheckParameters(registration, method);
        CheckResult(registration, method);

        registration.Definition = method;
        registration.SensitiveParameters = (method.Parameters ?? []).Where(p => p.Sensitive).Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void CheckParameters(CkMethodRegistration registration, CkMethodDto method)
    {
        var properties = registration.ParametersType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null && p.SetMethod != null)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var parameters = method.Parameters ?? [];

        foreach (var parameter in parameters)
        {
            if (!properties.TryGetValue(parameter.Name, out var property))
            {
                throw Invalid(registration,
                    $"parameter '{parameter.Name}' of the method has no property on {registration.ParametersType.Name}. " +
                    "Use the generated {Type}{Method}Parameters record of the model");
            }

            if (!IsCompatible(parameter.ValueType, property.PropertyType))
            {
                throw Invalid(registration,
                    $"parameter '{parameter.Name}' is {parameter.ValueType} in the model but property " +
                    $"{registration.ParametersType.Name}.{property.Name} is {property.PropertyType.Name}");
            }
        }

        var known = parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownRequired = properties.Values.FirstOrDefault(p =>
            p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null &&
            !known.Contains(p.Name));
        if (unknownRequired != null)
        {
            throw Invalid(registration,
                $"{registration.ParametersType.Name}.{unknownRequired.Name} is required but the method has no such " +
                "parameter, so no call could ever succeed");
        }
    }

    private static void CheckResult(CkMethodRegistration registration, CkMethodDto method)
    {
        if (method.Result != null && registration.ResultType == null)
        {
            throw Invalid(registration,
                "the method declares a result, but the handler implements ICkMethodHandler<TParameters> " +
                "(no result). Implement ICkMethodHandler<TParameters, TResult> with the generated {Type}{Method}Result");
        }

        if (method.Result == null && registration.ResultType != null)
        {
            throw Invalid(registration,
                $"the method declares no result, but the handler implements ICkMethodHandler<TParameters, {registration.ResultType.Name}>. " +
                "Implement ICkMethodHandler<TParameters>");
        }
    }

    // Only the unambiguous scalar types are checked; records, enums and arrays of the model are generated types.
    private static bool IsCompatible(AttributeValueTypesDto valueType, Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return valueType switch
        {
            AttributeValueTypesDto.String => type == typeof(string),
            AttributeValueTypesDto.Boolean => type == typeof(bool),
            AttributeValueTypesDto.Int => type == typeof(int),
            AttributeValueTypesDto.Int64 => type == typeof(long),
            AttributeValueTypesDto.Double => type == typeof(double),
            AttributeValueTypesDto.DateTime => type == typeof(DateTime),
            AttributeValueTypesDto.DateTimeOffset => type == typeof(DateTimeOffset),
            AttributeValueTypesDto.TimeSpan => type == typeof(TimeSpan),
            _ => true
        };
    }

    private static InvalidOperationException Invalid(CkMethodRegistration registration, string reason) =>
        new($"CK method handler registration for '{registration.MethodId}' ({registration.HandlerType.Name}) is " +
            $"invalid: {reason}.");
}
