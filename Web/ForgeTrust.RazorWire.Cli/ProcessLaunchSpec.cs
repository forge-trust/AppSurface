namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes how to launch an external process.
/// </summary>
public sealed class ProcessLaunchSpec
{
    /// <summary>
    /// Gets the executable file name.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the argument tokens passed to the process.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets environment variable overrides applied for process startup.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Gets the working directory for the process.
    /// </summary>
    public string WorkingDirectory { get; init; } = string.Empty;
}
