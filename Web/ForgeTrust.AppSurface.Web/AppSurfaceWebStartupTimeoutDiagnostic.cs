namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Builds operator-facing details for AppSurface Web startup watchdog failures.
/// </summary>
/// <remarks>
/// The diagnostic intentionally keeps to process and host-shape facts that are safe to log: sandbox markers, startup
/// phase, directories, static-asset mode, and endpoint-shaped command-line arguments. It avoids inspecting arbitrary app
/// configuration values, which may contain secrets.
/// </remarks>
internal sealed record AppSurfaceWebStartupTimeoutDiagnostic(
    TimeSpan StartupTimeout,
    string StartupPhase,
    string CurrentDirectory,
    string BaseDirectory,
    bool StaticWebAssetsEnabled,
    IReadOnlyList<string> StartupArgs,
    IReadOnlyDictionary<string, string> SandboxEnvironment)
{
    private static readonly string[] SandboxEnvironmentVariables =
    [
        "CODEX_SANDBOX",
        "CODEX_SANDBOX_NETWORK_DISABLED"
    ];

    /// <summary>
    /// Gets a value indicating whether the process environment contains a known sandbox marker.
    /// </summary>
    internal bool SandboxDetected => SandboxEnvironment.Count > 0;

    /// <summary>
    /// Gets a display-safe summary of detected sandbox markers.
    /// </summary>
    internal string SandboxSummary => SandboxDetected
        ? string.Join(", ", SandboxEnvironment.Select(pair => $"{pair.Key}={pair.Value}"))
        : "none detected";

    /// <summary>
    /// Gets the concrete next step operators should try first.
    /// </summary>
    internal string RecommendedAction => SandboxDetected
        ? "Detected a Codex sandbox. Rerun the command outside the sandbox, or with the runner's approved unsandboxed/escalated permission, before investigating package layout, static web assets, or hosted services."
        : "No sandbox marker was detected. Check static web asset discovery, package layout, endpoint binding, and hosted services that block StartAsync before Kestrel binds.";

    /// <summary>
    /// Gets endpoint-related command-line arguments rendered for diagnostics.
    /// </summary>
    internal string StartupArgsSummary => StartupArgs.Count == 0
        ? "<none>"
        : string.Join(" ", SelectEndpointArguments(StartupArgs).Select(QuoteArgument));

    /// <summary>
    /// Creates a startup-timeout diagnostic from the current process environment.
    /// </summary>
    /// <param name="startupTimeout">Configured startup watchdog timeout.</param>
    /// <param name="startupPhase">Startup phase observed when the watchdog fired.</param>
    /// <param name="currentDirectory">Current working directory used by the host.</param>
    /// <param name="baseDirectory">Application base directory used for dependency and asset resolution.</param>
    /// <param name="staticWebAssetsEnabled">Whether AppSurface static web asset loading was enabled.</param>
    /// <param name="startupArgs">Effective startup arguments after AppSurface development-port resolution.</param>
    /// <param name="environmentReader">Environment reader used to detect sandbox markers.</param>
    /// <returns>A diagnostic object suitable for structured logging and tests.</returns>
    internal static AppSurfaceWebStartupTimeoutDiagnostic Create(
        TimeSpan startupTimeout,
        string startupPhase,
        string currentDirectory,
        string baseDirectory,
        bool staticWebAssetsEnabled,
        IReadOnlyList<string> startupArgs,
        Func<string, string?> environmentReader)
    {
        ArgumentNullException.ThrowIfNull(environmentReader);

        var sandboxEnvironment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in SandboxEnvironmentVariables)
        {
            var value = environmentReader(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                sandboxEnvironment[variable] = value;
            }
        }

        return new AppSurfaceWebStartupTimeoutDiagnostic(
            startupTimeout,
            string.IsNullOrWhiteSpace(startupPhase) ? "unknown" : startupPhase,
            currentDirectory,
            baseDirectory,
            staticWebAssetsEnabled,
            startupArgs,
            sandboxEnvironment);
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;
    }

    private static IEnumerable<string> SelectEndpointArguments(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (IsEndpointArgumentAssignment(argument))
            {
                yield return argument;
                continue;
            }

            if (!IsEndpointArgumentName(argument))
            {
                continue;
            }

            yield return argument;
            if (index + 1 < arguments.Count && !LooksLikeOption(arguments[index + 1]))
            {
                yield return arguments[index + 1];
                index++;
            }
        }
    }

    private static bool IsEndpointArgumentAssignment(string argument)
    {
        var equalsIndex = argument.IndexOf('=', StringComparison.Ordinal);
        if (equalsIndex <= 0)
        {
            return false;
        }

        return IsEndpointArgumentName(argument[..equalsIndex]);
    }

    private static bool IsEndpointArgumentName(string argument)
    {
        var normalized = argument.TrimStart('-').Replace("__", ":", StringComparison.Ordinal);
        return normalized.Equals("urls", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("port", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("http_ports", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("https_ports", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ASPNETCORE_HTTP_PORTS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ASPNETCORE_HTTPS_PORTS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("DOTNET_HTTP_PORTS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("DOTNET_HTTPS_PORTS", StringComparison.OrdinalIgnoreCase)
            || IsKestrelEndpointUrlOrPortArgument(normalized);
    }

    private static bool LooksLikeOption(string argument)
    {
        return argument.StartsWith("-", StringComparison.Ordinal);
    }

    private static bool IsKestrelEndpointUrlOrPortArgument(string normalizedArgument)
    {
        const string prefix = "Kestrel:Endpoints:";
        if (!normalizedArgument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = normalizedArgument[prefix.Length..];
        var parts = suffix.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts is [_, "Url"] or [_, "Port"];
    }
}
