using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Validates resolved <see cref="Config{T}"/> and <see cref="ConfigStruct{T}"/> values with DataAnnotations.
/// The validator returns immediately for null values and scalar primitives, validates object and field
/// annotations before initialization completes, and traverses nested members only when callers opt in with
/// <see cref="ValidateObjectMembersAttribute"/> or <see cref="ValidateEnumeratedItemsAttribute"/>. Recursive
/// traversal tracks the active object path by reference so cycles terminate while shared objects can still be
/// reported at each reachable path. AppSurface owns this traversal and the
/// <see cref="ConfigurationValidationException"/> shape; the custom validator type overloads on the Microsoft
/// Options marker attributes are reported as unsupported failures rather than invoked.
/// </summary>
internal static class ConfigDataAnnotationsValidator
{
    /// <summary>
    /// Validates a resolved configuration value and throws <see cref="ConfigurationValidationException"/> when
    /// DataAnnotations failures are found. Call this after provider/default resolution and before config
    /// initialization is considered complete. Null and scalar values short-circuit, recursive validation is
    /// marker-gated, and callers should catch <see cref="ConfigurationValidationException"/> to inspect the
    /// structured failures.
    /// </summary>
    /// <param name="key">The configuration key being initialized.</param>
    /// <param name="configType">The concrete configuration wrapper type being initialized.</param>
    /// <param name="valueType">The resolved value type that is being validated.</param>
    /// <param name="value">The resolved provider or default value.</param>
    /// <exception cref="ConfigurationValidationException">
    /// Thrown when the resolved value or an opted-in nested value violates DataAnnotations validation rules.
    /// </exception>
    public static void Validate(
        string key,
        Type configType,
        Type valueType,
        object? value)
    {
        if (value == null || ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return;
        }

        var failures = new List<ConfigurationValidationFailure>();
        var activePath = new HashSet<object>(ReferenceEqualityComparer.Instance);

        ValidateNode(
            key,
            configType,
            valueType,
            value,
            path: null,
            activePath,
            failures);

        if (failures.Count > 0)
        {
            throw new ConfigurationValidationException(key, configType, valueType, failures);
        }
    }

    private static void ValidateNode(
        string key,
        Type configType,
        Type valueType,
        object value,
        string? path,
        HashSet<object> activePath,
        List<ConfigurationValidationFailure> failures)
    {
        if (!TrackVisit(value, activePath))
        {
            return;
        }

        try
        {
            var results = new List<ValidationResult>();
            var context = new ValidationContext(value);

            Validator.TryValidateObject(
                value,
                context,
                results,
                validateAllProperties: true);

            foreach (var result in results)
            {
                failures.Add(ConfigValidationFailureFactory.FromValidationResult(key, configType, valueType, path, result));
            }

            ValidateFieldAnnotations(key, configType, valueType, value, path, failures);

            foreach (var property in GetValidatableProperties(value.GetType()))
            {
                ValidateProperty(key, configType, valueType, value, property, path, activePath, failures);
            }

            foreach (var field in GetValidatableFields(value.GetType()))
            {
                ValidateField(key, configType, valueType, value, field, path, activePath, failures);
            }
        }
        finally
        {
            if (!value.GetType().IsValueType)
            {
                activePath.Remove(value);
            }
        }
    }

    private static void ValidateFieldAnnotations(
        string key,
        Type configType,
        Type valueType,
        object parent,
        string? parentPath,
        List<ConfigurationValidationFailure> failures)
    {
        foreach (var field in GetValidatableFields(parent.GetType()))
        {
            var attributes = field.GetCustomAttributes<ValidationAttribute>(inherit: true).ToArray();
            if (attributes.Length == 0)
            {
                continue;
            }

            var results = new List<ValidationResult>();
            var context = new ValidationContext(parent)
            {
                MemberName = field.Name
            };

            Validator.TryValidateValue(
                field.GetValue(parent),
                context,
                results,
                attributes);

            foreach (var result in results)
            {
                failures.Add(ConfigValidationFailureFactory.FromValidationResult(
                    key,
                    configType,
                    valueType,
                    parentPath,
                    result,
                    field.Name));
            }
        }
    }

    private static void ValidateProperty(
        string key,
        Type configType,
        Type valueType,
        object parent,
        PropertyInfo property,
        string? parentPath,
        HashSet<object> activePath,
        List<ConfigurationValidationFailure> failures)
    {
        var objectMembersAttribute = property.GetCustomAttribute<ValidateObjectMembersAttribute>();
        var enumeratedItemsAttribute = property.GetCustomAttribute<ValidateEnumeratedItemsAttribute>();
        if (objectMembersAttribute == null && enumeratedItemsAttribute == null)
        {
            return;
        }

        var memberPath = CombinePath(parentPath, property.Name);
        var memberValue = property.GetValue(parent);

        ValidateOptInMember(
            key,
            configType,
            valueType,
            memberValue,
            memberPath,
            objectMembersAttribute,
            enumeratedItemsAttribute,
            activePath,
            failures);
    }

    private static void ValidateField(
        string key,
        Type configType,
        Type valueType,
        object parent,
        FieldInfo field,
        string? parentPath,
        HashSet<object> activePath,
        List<ConfigurationValidationFailure> failures)
    {
        var objectMembersAttribute = field.GetCustomAttribute<ValidateObjectMembersAttribute>();
        var enumeratedItemsAttribute = field.GetCustomAttribute<ValidateEnumeratedItemsAttribute>();
        if (objectMembersAttribute == null && enumeratedItemsAttribute == null)
        {
            return;
        }

        var memberPath = CombinePath(parentPath, field.Name);
        var memberValue = field.GetValue(parent);

        ValidateOptInMember(
            key,
            configType,
            valueType,
            memberValue,
            memberPath,
            objectMembersAttribute,
            enumeratedItemsAttribute,
            activePath,
            failures);
    }

    private static void ValidateOptInMember(
        string key,
        Type configType,
        Type valueType,
        object? memberValue,
        string memberPath,
        ValidateObjectMembersAttribute? objectMembersAttribute,
        ValidateEnumeratedItemsAttribute? enumeratedItemsAttribute,
        HashSet<object> activePath,
        List<ConfigurationValidationFailure> failures)
    {
        if (objectMembersAttribute?.Validator != null)
        {
            failures.Add(CreateUnsupportedValidatorFailure(
                key,
                configType,
                valueType,
                memberPath,
                nameof(ValidateObjectMembersAttribute)));
        }

        if (enumeratedItemsAttribute?.Validator != null)
        {
            failures.Add(CreateUnsupportedValidatorFailure(
                key,
                configType,
                valueType,
                memberPath,
                nameof(ValidateEnumeratedItemsAttribute)));
        }

        if (memberValue == null)
        {
            return;
        }

        if (objectMembersAttribute != null && !ConfigScalarTypes.IsScalar(memberValue.GetType()))
        {
            ValidateNode(
                key,
                configType,
                valueType,
                memberValue,
                memberPath,
                activePath,
                failures);
        }

        if (enumeratedItemsAttribute != null && memberValue is IEnumerable enumerable && memberValue is not string)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                if (item != null && !ConfigScalarTypes.IsScalar(item.GetType()))
                {
                    ValidateNode(
                        key,
                        configType,
                        valueType,
                        item,
                        $"{memberPath}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
                        activePath,
                        failures);
                }

                index++;
            }
        }
    }

    private static ConfigurationValidationFailure CreateUnsupportedValidatorFailure(
        string key,
        Type configType,
        Type valueType,
        string memberPath,
        string attributeName) =>
        new(
            key,
            configType,
            valueType,
            [memberPath],
            $"{attributeName} custom validator types are not supported by AppSurface Config validation.");

    private static IEnumerable<PropertyInfo> GetValidatableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => property.GetMethod is { IsPublic: true });

    private static IEnumerable<FieldInfo> GetValidatableFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public);

    private static bool TrackVisit(object value, HashSet<object> visited)
    {
        if (value.GetType().IsValueType)
        {
            return true;
        }

        return visited.Add(value);
    }

    private static string CombinePath(string? prefix, string memberName) =>
        string.IsNullOrEmpty(prefix)
            ? memberName
            : $"{prefix}.{memberName}";

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
