using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Bounds untrusted configuration inputs and retained diagnostic identities.</summary>
/// <remarks>All limits must be positive. Exceeding a limit is terminal; no partial source is published.</remarks>
public sealed class ConfigResourceOptions
{
    /// <summary>Gets or sets maximum bytes per JSON file layer; defaults to 16 MiB.</summary>
    public long MaxFileBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>Gets or sets maximum files per environment; defaults to 256.</summary>
    public int MaxFilesPerEnvironment { get; set; } = 256;
    /// <summary>Gets or sets maximum captured environment entries; defaults to 65,536.</summary>
    public int MaxEnvironmentEntries { get; set; } = 65_536;
    /// <summary>Gets or sets maximum UTF-8 bytes in environment names and values; defaults to 64 MiB.</summary>
    public long MaxEnvironmentBytes { get; set; } = 64 * 1024 * 1024;
    /// <summary>Gets or sets maximum distinct notice identities per operation and in process log suppression; defaults to 4,096.</summary>
    /// <remarks>Operation overflow marks audit notice evidence incomplete; duplicate identities do not consume capacity.</remarks>
    public int MaxNoticeIdentities { get; set; } = 4096;
    /// <summary>Gets or sets maximum rendered identifier characters before a digest; defaults to 256.</summary>
    public int MaxRenderedIdentifierCharacters { get; set; } = 256;
    /// <summary>Gets or sets the maximum object binding depth; defaults to 32.</summary>
    public int MaxBindingDepth { get; set; } = 32;
    /// <summary>Gets or sets the aggregate remote-audit deadline; defaults to 30 seconds and is capped at uint.MaxValue - 1 milliseconds.</summary>
    /// <remarks>The upper bound keeps the deadline representable by the timer APIs used for cancellation.</remarks>
    public TimeSpan AuditTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets or sets the maximum uncached remote lookups in one audit; defaults to 256.</summary>
    public int MaxAuditRemoteLookups { get; set; } = 256;
    /// <summary>Gets or sets the maximum concurrent remote lookups in one audit; defaults to four.</summary>
    public int MaxAuditConcurrency { get; set; } = 4;

    /// <summary>Validates and snapshots limits before any provider publishes its indexes.</summary>
    internal ConfigResourceOptions Snapshot()
    {
        if (MaxFileBytes <= 0 || MaxFilesPerEnvironment <= 0 || MaxEnvironmentEntries <= 0
            || MaxEnvironmentBytes <= 0 || MaxNoticeIdentities <= 0
            || MaxRenderedIdentifierCharacters <= 0 || MaxBindingDepth <= 0
            || AuditTimeout <= TimeSpan.Zero
            || MaxAuditRemoteLookups <= 0 || MaxAuditConcurrency <= 0)
        {
            throw new OptionsValidationException(nameof(ConfigResourceOptions), typeof(ConfigResourceOptions),
                ["Every configuration resource limit must be positive."]);
        }

        if (AuditTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new OptionsValidationException(nameof(ConfigResourceOptions), typeof(ConfigResourceOptions),
                [$"AuditTimeout must not exceed {uint.MaxValue - 1} milliseconds."]);
        }

        return (ConfigResourceOptions)MemberwiseClone();
    }
}
