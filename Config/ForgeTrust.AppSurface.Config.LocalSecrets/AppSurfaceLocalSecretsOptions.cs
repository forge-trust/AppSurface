namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Configures the AppSurface LocalSecrets provider.
/// </summary>
/// <remarks>
/// Defaults are intentionally fail-closed: LocalSecrets is development-only, claims keys once registered, and reports
/// local store failures as terminal diagnostics instead of falling through to lower-priority file configuration.
/// </remarks>
public sealed class AppSurfaceLocalSecretsOptions
{
    /// <summary>
    /// Gets or sets the posture mode for local secret resolution.
    /// </summary>
    public LocalSecretsPostureMode Posture { get; set; } = LocalSecretsPostureMode.DevelopmentOnly;

    /// <summary>
    /// Gets or sets the application identity used in the platform store namespace.
    /// </summary>
    /// <remarks>
    /// Leave unset to infer an identity from the entry assembly or current directory. Override this when multiple apps
    /// share a binary name or when command-line workflows need a stable package-independent identity.
    /// </remarks>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Gets or sets an optional namespace prefix applied before the AppSurface config key.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets the documentation hint emitted in local secret diagnostics.
    /// </summary>
    public string DocsHint { get; set; } = "local-secrets-without-a-remote-vault";

    /// <summary>
    /// Gets or sets an explicit Linux <c>secret-tool</c> executable path for nonstandard trusted installs.
    /// </summary>
    /// <remarks>
    /// Leave unset to use AppSurface's trusted Linux system candidates only: <c>/usr/bin/secret-tool</c>, then
    /// <c>/bin/secret-tool</c>. Set this only when the binary is trusted and verified with a command such as
    /// <c>test -x /absolute/path/to/secret-tool</c>. Relative, empty, missing, directory, and non-executable paths are
    /// rejected. This option is Linux-only; macOS and Windows use their native credential stores.
    /// </remarks>
    public string? LinuxSecretToolPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether local store failures stop lower-priority provider resolution.
    /// </summary>
    /// <remarks>
    /// Keep the default enabled for secret posture. Disabling this escape hatch makes unavailable stores behave like
    /// missing values and can mask secrets from files.
    /// </remarks>
    public bool FailClosedOnStoreFailure { get; set; } = true;

    /// <summary>
    /// Gets or sets development-like environment names accepted by <see cref="LocalSecretsPostureMode.DevelopmentOnly"/>.
    /// </summary>
    public ISet<string> DevelopmentEnvironmentNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Development",
            "Local",
            "Dev"
        };
}
