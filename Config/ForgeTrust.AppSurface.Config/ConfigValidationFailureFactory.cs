using System.ComponentModel.DataAnnotations;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Creates <see cref="ConfigurationValidationFailure"/> instances from DataAnnotations results while
/// preserving AppSurface's member-path formatting contract.
/// </summary>
internal static class ConfigValidationFailureFactory
{
    /// <summary>
    /// Converts a <see cref="ValidationResult"/> into a configuration-validation failure payload.
    /// </summary>
    /// <param name="key">The configuration key being validated.</param>
    /// <param name="configType">The concrete configuration wrapper type.</param>
    /// <param name="valueType">The resolved value type being validated.</param>
    /// <param name="path">
    /// Optional member path prefix. When present, validation-result member names are combined under this
    /// path using dot notation, and the path itself is used when the result has no member names.
    /// </param>
    /// <param name="result">The validation result to convert.</param>
    /// <param name="defaultMemberName">
    /// Optional fallback member name used when <paramref name="result"/> has no member names, such as
    /// field-level validation where DataAnnotations did not bind the member name itself.
    /// </param>
    /// <returns>A normalized <see cref="ConfigurationValidationFailure"/>.</returns>
    public static ConfigurationValidationFailure FromValidationResult(
        string key,
        Type configType,
        Type valueType,
        string? path,
        ValidationResult result,
        string? defaultMemberName = null)
    {
        var resultMemberNames = result.MemberNames.ToList();
        if (resultMemberNames.Count == 0 && defaultMemberName != null)
        {
            resultMemberNames.Add(defaultMemberName);
        }

        var memberNames = resultMemberNames
            .Select(memberName => PrefixMemberName(path, memberName))
            .Where(memberName => !string.IsNullOrWhiteSpace(memberName))
            .ToList();

        if (memberNames.Count == 0 && path != null)
        {
            memberNames.Add(path);
        }

        return new ConfigurationValidationFailure(
            key,
            configType,
            valueType,
            memberNames,
            result.ErrorMessage ?? "The configuration value is invalid.");
    }

    private static string PrefixMemberName(string? path, string memberName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return memberName;
        }

        return string.IsNullOrWhiteSpace(memberName)
            ? path
            : $"{path}.{memberName}";
    }
}
