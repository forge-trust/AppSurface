using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// The exception thrown when a strongly typed configuration value fails validation during initialization.
/// </summary>
public sealed class ConfigurationValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationException"/> class.
    /// </summary>
    /// <param name="key">The configuration key being initialized.</param>
    /// <param name="configType">The concrete configuration wrapper type being initialized.</param>
    /// <param name="valueType">The resolved value type that was validated.</param>
    /// <param name="failures">The validation failures returned for the configuration value.</param>
    public ConfigurationValidationException(
        string key,
        Type configType,
        Type valueType,
        IReadOnlyList<ConfigurationValidationFailure> failures)
        : this(key, configType, valueType, SnapshotFailures(failures), failuresAlreadySnapshotted: true)
    {
    }

    private ConfigurationValidationException(
        string key,
        Type configType,
        Type valueType,
        IReadOnlyList<ConfigurationValidationFailure> failures,
        bool failuresAlreadySnapshotted)
        : base(BuildMessage(key, configType, valueType, failures))
    {
        Key = key;
        ConfigType = configType;
        ValueType = valueType;
        Failures = failuresAlreadySnapshotted ? failures : SnapshotFailures(failures);
    }

    /// <summary>
    /// Gets the configuration key being initialized.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the concrete configuration wrapper type being initialized.
    /// </summary>
    public Type ConfigType { get; }

    /// <summary>
    /// Gets the resolved value type that was validated.
    /// </summary>
    public Type ValueType { get; }

    /// <summary>
    /// Gets the validation failures returned for the configuration value.
    /// </summary>
    public IReadOnlyList<ConfigurationValidationFailure> Failures { get; }

    private static IReadOnlyList<ConfigurationValidationFailure> SnapshotFailures(
        IReadOnlyList<ConfigurationValidationFailure> failures) =>
        Array.AsReadOnly(failures.ToArray());

    private static string BuildMessage(
        string key,
        Type configType,
        Type valueType,
        IReadOnlyList<ConfigurationValidationFailure> failures)
    {
        var orderedFailures = failures
            .OrderBy(failure => GetMemberLabel(failure, valueType), StringComparer.Ordinal)
            .ThenBy(failure => failure.Message, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append("Configuration validation failed for key '")
            .Append(key)
            .Append("' (")
            .Append(configType.Name)
            .Append(" -> ")
            .Append(valueType.Name)
            .Append("): ")
            .Append(orderedFailures.Count)
            .Append(" error(s).");

        foreach (var failure in orderedFailures)
        {
            builder.Append(Environment.NewLine)
                .Append("- ")
                .Append(GetMemberLabel(failure, valueType))
                .Append(": ")
                .Append(failure.Message);
        }

        return builder.ToString();
    }

    private static string GetMemberLabel(ConfigurationValidationFailure failure, Type valueType)
    {
        if (failure.MemberNames.Count == 0)
        {
            return ConfigScalarTypes.IsScalar(valueType) ? "<value>" : "<object>";
        }

        return string.Join(
            ", ",
            failure.MemberNames.Order(StringComparer.Ordinal));
    }
}
