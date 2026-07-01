namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Describes a typed Google Secret Manager configuration resolution.
/// </summary>
/// <typeparam name="T">The requested configuration type.</typeparam>
public sealed class GoogleSecretManagerConfigResolution<T>
{
    private GoogleSecretManagerConfigResolution(
        GoogleSecretManagerResultStatus status,
        T? value,
        ConfigProviderTerminalDiagnostic? diagnostic,
        string? source)
    {
        Status = status;
        Value = value;
        Diagnostic = diagnostic;
        Source = source;
    }

    /// <summary>
    /// Gets the resolution status.
    /// </summary>
    public GoogleSecretManagerResultStatus Status { get; }

    /// <summary>
    /// Gets the converted value when <see cref="Status"/> is <see cref="GoogleSecretManagerResultStatus.Found"/>.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the display-safe terminal diagnostic when resolution failed for a claimed key.
    /// </summary>
    public ConfigProviderTerminalDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Gets the provider source name.
    /// </summary>
    public string? Source { get; }

    /// <summary>
    /// Creates an unclaimed resolution.
    /// </summary>
    /// <returns>An unclaimed resolution.</returns>
    public static GoogleSecretManagerConfigResolution<T> Unclaimed() =>
        new(GoogleSecretManagerResultStatus.Unclaimed, default, null, null);

    /// <summary>
    /// Creates a found resolution.
    /// </summary>
    /// <param name="value">The converted value.</param>
    /// <param name="source">The source name.</param>
    /// <returns>A found resolution.</returns>
    public static GoogleSecretManagerConfigResolution<T> Found(T? value, string source) =>
        new(GoogleSecretManagerResultStatus.Found, value, null, source);

    /// <summary>
    /// Creates a failed claimed-key resolution.
    /// </summary>
    /// <param name="status">The failed status.</param>
    /// <param name="diagnostic">The display-safe diagnostic.</param>
    /// <param name="source">The source name.</param>
    /// <returns>A failed resolution.</returns>
    public static GoogleSecretManagerConfigResolution<T> Failed(
        GoogleSecretManagerResultStatus status,
        ConfigProviderTerminalDiagnostic diagnostic,
        string source) =>
        new(status, default, diagnostic, source);
}
