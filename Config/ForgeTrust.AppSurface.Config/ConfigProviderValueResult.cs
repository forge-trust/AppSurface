namespace ForgeTrust.AppSurface.Config;

/// <summary>The three mutually exclusive outcomes of one provider request.</summary>
public enum ConfigProviderValueStatus
{
    /// <summary>No value exists; lower-priority resolution may continue.</summary>
    Missing = 0,
    /// <summary>A non-null value was found, possibly through a reported compatibility alias.</summary>
    Found = 1,
    /// <summary>Resolution must stop; lower-priority fallback is suppressed.</summary>
    Terminal = 2
}

/// <summary>A provider value or terminal diagnostic carried atomically in a single immutable result.</summary>
/// <typeparam name="T">The requested value type.</typeparam>
/// <remarks>Missing is distinct from a found default value such as zero or false. Values must never be logged.</remarks>
public sealed class ConfigProviderValueResult<T>
{
    private ConfigProviderValueResult(ConfigProviderValueStatus status, T? value,
        ConfigProviderTerminalDiagnostic? diagnostic, IReadOnlyList<ConfigProviderNotice> notices)
    {
        Status = status;
        Value = value;
        Diagnostic = diagnostic;
        Notices = notices;
    }

    /// <summary>Gets the exclusive outcome.</summary>
    public ConfigProviderValueStatus Status { get; }
    /// <summary>Gets the value for Found; otherwise the default value.</summary>
    public T? Value { get; }
    /// <summary>Gets the diagnostic only for Terminal.</summary>
    public ConfigProviderTerminalDiagnostic? Diagnostic { get; }
    /// <summary>Gets the defensively copied notices only for Found.</summary>
    public IReadOnlyList<ConfigProviderNotice> Notices { get; }

    /// <summary>Creates an absent result without diagnostics, notices, or a value.</summary>
    /// <returns>A missing outcome.</returns>
    public static ConfigProviderValueResult<T> Missing() => new(ConfigProviderValueStatus.Missing, default, null, []);

    /// <summary>Creates a successful result with an immutable notice collection.</summary>
    /// <param name="value">A non-null value, including a value-type default.</param>
    /// <param name="notices">Non-null display-safe notices; the array and members must be non-null.</param>
    /// <returns>A found outcome.</returns>
    /// <exception cref="ArgumentNullException">The value, array, or a notice is null.</exception>
    public static ConfigProviderValueResult<T> Found(T value, params ConfigProviderNotice[] notices)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(notices);
        foreach (var notice in notices)
        {
            ArgumentNullException.ThrowIfNull(notice, nameof(notices));
        }

        return new(ConfigProviderValueStatus.Found, value, null, Array.AsReadOnly((ConfigProviderNotice[])notices.Clone()));
    }

    /// <summary>Creates a terminal outcome with no value or notices.</summary>
    /// <param name="diagnostic">The display-safe failure that suppresses fallback.</param>
    /// <returns>A terminal outcome.</returns>
    /// <exception cref="ArgumentNullException">The diagnostic is null.</exception>
    public static ConfigProviderValueResult<T> Terminal(ConfigProviderTerminalDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(ConfigProviderValueStatus.Terminal, default, diagnostic, []);
    }
}
