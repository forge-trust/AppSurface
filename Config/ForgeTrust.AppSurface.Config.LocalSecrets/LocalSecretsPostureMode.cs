namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Defines where LocalSecrets is allowed to participate in configuration resolution.
/// </summary>
public enum LocalSecretsPostureMode
{
    /// <summary>Disable the LocalSecrets provider and command runner.</summary>
    Disabled = 0,

    /// <summary>Allow LocalSecrets only for development-like environments. This is the default.</summary>
    DevelopmentOnly = 1,

    /// <summary>Allow LocalSecrets for explicit single-machine self-hosting with no team-vault guarantees.</summary>
    SingleMachineSelfHosted = 2
}
