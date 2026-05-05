using System.Reflection;

namespace ForgeTrust.Runnable.Config;

/// <summary>
/// Validates required resolved presence for strongly typed configuration wrappers.
/// </summary>
internal static class ConfigPresenceValidator
{
    private const string RequiredPresenceMessage = "A value is required for this configuration key.";

    /// <summary>
    /// Throws <see cref="ConfigurationValidationException"/> when a wrapper marked with
    /// <see cref="ConfigKeyRequiredAttribute"/> has no resolved provider or default value.
    /// </summary>
    /// <param name="key">The configuration key being initialized.</param>
    /// <param name="configType">The concrete configuration wrapper type being initialized.</param>
    /// <param name="valueType">The declared configuration value type.</param>
    /// <param name="hasValue">Whether provider/default resolution produced a value.</param>
    public static void Validate(
        string key,
        Type configType,
        Type valueType,
        bool hasValue)
    {
        if (hasValue || configType.GetCustomAttribute<ConfigKeyRequiredAttribute>(inherit: true) == null)
        {
            return;
        }

        var failure = new ConfigurationValidationFailure(
            key,
            configType,
            valueType,
            Array.Empty<string>(),
            RequiredPresenceMessage);

        throw new ConfigurationValidationException(key, configType, valueType, [failure]);
    }
}
