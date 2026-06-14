namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Defines where LocalSecrets is allowed to participate in configuration resolution.
/// </summary>
public enum LocalSecretsPostureMode
{
    /// <summary>Allow LocalSecrets only for development-like environments. This is the CLR and options default.</summary>
    DevelopmentOnly = 0,

    /// <summary>Disable the LocalSecrets provider and command runner.</summary>
    Disabled = 1,

    /// <summary>Allow LocalSecrets for explicit single-machine self-hosting with no team-vault guarantees.</summary>
    SingleMachineSelfHosted = 2
}
