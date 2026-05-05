using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace ForgeTrust.Runnable.Config;

internal static class ConfigScalarValueValidator
{
    public static void Validate(
        string key,
        object config,
        Type valueType,
        object? value,
        Func<object, ValidationContext, IEnumerable<ValidationResult>?> validateValue)
    {
        if (value == null || !ConfigScalarTypes.IsScalar(valueType))
        {
            return;
        }

        var configType = config.GetType();
        var failures = new List<ConfigurationValidationFailure>();
        var validationContext = new ValidationContext(config)
        {
            DisplayName = key,
            MemberName = null
        };

        ValidateAttributes(key, configType, valueType, value, validationContext, failures);
        ValidateHook(key, configType, valueType, value, validationContext, validateValue, failures);

        if (failures.Count > 0)
        {
            throw new ConfigurationValidationException(key, configType, valueType, failures);
        }
    }

    private static void ValidateAttributes(
        string key,
        Type configType,
        Type valueType,
        object value,
        ValidationContext validationContext,
        List<ConfigurationValidationFailure> failures)
    {
        var attributes = configType
            .GetCustomAttributes<ConfigValueValidationAttribute>(inherit: true)
            .ToArray();
        if (attributes.Length == 0)
        {
            return;
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateValue(value, validationContext, results, attributes);

        foreach (var result in results)
        {
            failures.Add(ConfigValidationFailureFactory.FromValidationResult(
                key,
                configType,
                valueType,
                path: null,
                result));
        }
    }

    private static void ValidateHook(
        string key,
        Type configType,
        Type valueType,
        object value,
        ValidationContext validationContext,
        Func<object, ValidationContext, IEnumerable<ValidationResult>?> validateValue,
        List<ConfigurationValidationFailure> failures)
    {
        var results = validateValue(value, validationContext);
        if (results == null)
        {
            return;
        }

        foreach (var result in results)
        {
            if (result == null || result == ValidationResult.Success)
            {
                continue;
            }

            failures.Add(ConfigValidationFailureFactory.FromValidationResult(
                key,
                configType,
                valueType,
                path: null,
                result));
        }
    }
}
