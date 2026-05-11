using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Defines a configuration object that can be initialized using a configuration manager.
/// </summary>
public interface IConfig
{
    /// <summary>
    /// Initializes the configuration object, resolving its provider or default value and failing fast
    /// with <see cref="ConfigurationValidationException"/> when required presence is not satisfied
    /// or when the resolved value violates object DataAnnotations rules or scalar value validation rules.
    /// Exceptions thrown by scalar <see cref="Config{T}.ValidateValue"/> or
    /// <see cref="ConfigStruct{T}.ValidateValue"/> overrides are not wrapped, so callers that activate
    /// config wrappers during startup should let unexpected programming errors fail the startup path.
    /// </summary>
    /// <param name="configManager">The configuration manager to use for retrieving values.</param>
    /// <param name="environmentProvider">The environment provider.</param>
    /// <param name="key">The root configuration key for this object.</param>
    /// <exception cref="ConfigurationValidationException">
    /// Thrown when the wrapper requires a value and no provider/default value resolves, or when the resolved
    /// provider value or default value violates object DataAnnotations or scalar validation rules.
    /// </exception>
    /// <exception cref="Exception">
    /// Thrown when a concrete scalar validation override throws; override exceptions are not wrapped.
    /// </exception>
    void Init(
        IConfigManager configManager,
        IEnvironmentProvider environmentProvider,
        string key);
}
