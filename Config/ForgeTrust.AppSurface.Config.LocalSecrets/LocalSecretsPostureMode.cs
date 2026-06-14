namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Defines where LocalSecrets is allowed to participate in configuration resolution.
/// </summary>
/// <remarks>
/// Numeric values are part of the public AppSurface LocalSecrets contract and must remain stable. The zero value is
/// <see cref="DevelopmentOnly"/> so CLR default enum initialization matches the documented provider default.
/// </remarks>
public enum LocalSecretsPostureMode
{
    /// <summary>Allow LocalSecrets only for development-like environments. This is the CLR and options default.</summary>
    DevelopmentOnly = 0,

    /// <summary>Disable the LocalSecrets provider and command runner.</summary>
    Disabled = 1,

    /// <summary>Allow LocalSecrets for explicit single-machine self-hosting with no team-vault guarantees.</summary>
    SingleMachineSelfHosted = 2
}
