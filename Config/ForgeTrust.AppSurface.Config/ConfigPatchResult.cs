namespace ForgeTrust.AppSurface.Config;

/// <summary>The result of an isolated, all-present-children-validated patch transaction.</summary>
internal enum ConfigPatchStatus
{
    NotApplied,
    Applied,
    Terminal
}

/// <summary>Publishes a candidate only after all present patch inputs have passed conversion and validation.</summary>
internal sealed class ConfigPatchResult<T>
{
    private ConfigPatchResult(ConfigPatchStatus status, T? value, ConfigProviderTerminalDiagnostic? diagnostic)
    {
        Status = status;
        Value = value;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the transaction outcome.</summary>
    internal ConfigPatchStatus Status { get; }
    /// <summary>Gets the isolated value only when Applied.</summary>
    internal T? Value { get; }
    /// <summary>Gets the diagnostic only when Terminal.</summary>
    internal ConfigProviderTerminalDiagnostic? Diagnostic { get; }
    /// <summary>Creates a result with no applicable children.</summary>
    internal static ConfigPatchResult<T> NotApplied() => new(ConfigPatchStatus.NotApplied, default, null);
    /// <summary>Publishes a non-null isolated candidate after successful validation.</summary>
    internal static ConfigPatchResult<T> Applied(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ConfigPatchStatus.Applied, value, null);
    }
    /// <summary>Rejects the entire transaction without exposing a partially mutated value.</summary>
    internal static ConfigPatchResult<T> Terminal(ConfigProviderTerminalDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(ConfigPatchStatus.Terminal, default, diagnostic);
    }
}
