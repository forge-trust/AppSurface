namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes one validation failure found while initializing a strongly typed configuration value.
/// </summary>
public sealed record ConfigurationValidationFailure
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationValidationFailure"/> class.
    /// </summary>
    /// <param name="key">The configuration key being initialized.</param>
    /// <param name="configType">The concrete configuration wrapper type being initialized.</param>
    /// <param name="valueType">The resolved value type that was validated.</param>
    /// <param name="memberNames">
    /// The member names or paths associated with the failure. Use an empty sequence for object-level failures.
    /// Values are defensively copied into an immutable snapshot owned by this instance.
    /// </param>
    /// <param name="message">The validation message.</param>
    public ConfigurationValidationFailure(
        string key,
        Type configType,
        Type valueType,
        IEnumerable<string> memberNames,
        string message)
    {
        Key = key;
        ConfigType = configType;
        ValueType = valueType;
        MemberNames = Array.AsReadOnly(memberNames.ToArray());
        Message = message;
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
    /// Gets the member names or paths associated with the failure. Object-level failures use an empty list
    /// rather than null; consumers should treat that empty collection as a valid failure payload. The returned
    /// list is an immutable snapshot owned by this instance.
    /// </summary>
    public IReadOnlyList<string> MemberNames { get; }

    /// <summary>
    /// Gets the validation message.
    /// </summary>
    public string Message { get; }
}
