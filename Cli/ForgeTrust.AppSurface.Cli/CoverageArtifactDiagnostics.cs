using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Describes a bounded, local storage diagnostic that never changes a primary coverage result.</summary>
/// <param name="Code">Stable ASCOV200–ASCOV211 identifier.</param>
/// <param name="Operation">Trusted operation name.</param>
/// <param name="Cause">Sanitized, bounded cause; arbitrary exception messages are forbidden.</param>
/// <param name="Recovery">Trusted recovery template, independent of report content.</param>
/// <param name="SideEffects">none, partial, complete, or unknown.</param>
/// <param name="RunId">Safe opaque run identifier, when available.</param>
/// <param name="ObservedBytes">An observed byte count, when known.</param>
/// <param name="AllowedBytes">The corresponding effective byte limit.</param>
/// <param name="SourceStillAvailable">Whether the original current output is still available.</param>
/// <param name="SourceReplaced">Whether the documented replacement already occurred.</param>
/// <param name="Trace">Sanitized bounded stack information for an unexpected local error.</param>
internal sealed record CoverageArtifactDiagnostic(
    string Code,
    string Operation,
    string Cause,
    string Recovery,
    string SideEffects = "none",
    string? RunId = null,
    long? ObservedBytes = null,
    long? AllowedBytes = null,
    bool? SourceStillAvailable = null,
    bool? SourceReplaced = null,
    string? Trace = null)
{
    /// <summary>Gets the stable store-open failure reason, when classification is available; it is never ownership authority.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the canonical reference anchor for the diagnostic.</summary>
    public string Documentation => "guides/coverage-storage.md#" + Code.ToLowerInvariant() + (Code switch
    {
        "ASCOV200" => "-history-not-saved",
        "ASCOV201" => "-store-degraded",
        "ASCOV202" => "-operation-busy",
        "ASCOV203" => "-inspect-incomplete",
        "ASCOV204" => "-maintain-partial",
        "ASCOV205" => "-restore-failed",
        "ASCOV206" => "-pin-state",
        "ASCOV207" => "-metadata-capacity",
        "ASCOV208" => "-invalid-storage-input",
        "ASCOV209" => "-implicit-collector-risk",
        "ASCOV210" => "-storage-growth",
        "ASCOV211" => "-settings-inspection-unavailable",
        _ => string.Empty
    });

    /// <summary>Gets the maximum automatic retry count after the named condition has been resolved.</summary>
    public int MaxRetries => 1;

    /// <summary>Gets the producing engine version for troubleshooting effective defaults.</summary>
    public string Version => CoverageArtifactInventory.ProducingVersion;

    /// <summary>Gets environment names relevant to local management, without their values.</summary>
    public IReadOnlyList<string> EnvironmentNames => ["APPSURFACE_ARTIFACT_HOME"];

    /// <summary>Gets the schema defining the fixed storage limits used by this diagnostic.</summary>
    public int PolicySchemaVersion => 1;

    /// <summary>Creates a cause-specific optional-storage failure without disclosing exception text.</summary>
    /// <param name="code">Stable diagnostic identifier.</param>
    /// <param name="operation">Trusted operation name.</param>
    /// <param name="exception">Failure whose type and bounded source frames may be displayed.</param>
    /// <param name="sideEffects">Known publication or mutation state.</param>
    internal static CoverageArtifactDiagnostic Failure(string code, string operation, Exception exception, string sideEffects = "none")
    {
        var failureKind = CoverageArtifactStoreFailure.Kind(exception);
        var cause = failureKind.HasValue ? CoverageArtifactStoreFailure.Cause(failureKind.Value) : exception switch
        {
            _ when code == "ASCOV202" => "A compatible coverage storage operation is already using this resource; the operation failed fast before mutation.",
            _ when code == "ASCOV208" => "The supplied coverage storage input is invalid.",
            UnauthorizedAccessException => "Permission denied; private current-user access is required.",
            TimeoutException => "The cooperative optional-storage deadline was reached.",
            PlatformNotSupportedException => "This root lacks a supported native storage capability.",
            InvalidDataException or JsonException => "Storage metadata or policy is invalid or uses an unsupported schema; preserve it for inspection.",
            OperationCanceledException => "The caller cancelled the operation.",
            _ => "Storage operation failed (" + CoverageArtifactDisplay.Bound(exception.GetType().Name) + "); inspect the local resource condition."
        };
        var recovery = failureKind.HasValue ? CoverageArtifactStoreFailure.Recovery(failureKind.Value) : code == "ASCOV202"
            ? "Wait for the compatible operation to finish, then retry once or choose a fresh output. Do not delete lock files."
            : code == "ASCOV208"
                ? "Supply valid command options and retry once."
            : code == "ASCOV200"
            ? "While the source remains current, pin it explicitly if preservation is required. After replacement, recovery requires an existing external copy; rerunning cannot recover history."
            : exception is PlatformNotSupportedException
                ? "Set APPSURFACE_ARTIFACT_HOME to a supported private writable local directory, inspect status, then retry once."
                : "Inspect coverage storage status --json, resolve the reported permission, space, capability or metadata condition, then retry once. Preserve unknown state; never delete lock files to force admission.";
        // Only stack frame method names are retained. Exception messages, arguments and file paths may contain secrets.
        var frames = new System.Diagnostics.StackTrace(exception, false).GetFrames();
        var trace = frames is null ? null : CoverageArtifactDisplay.Bound(string.Join(" > ", frames.Take(6).Select(frame => frame.GetMethod()?.Name ?? "unknown")));
        return new(code, operation, cause, recovery, sideEffects, Trace: trace) { Reason = failureKind?.ToString() ?? (exception switch { PlatformNotSupportedException => "CapabilityUnavailable", UnauthorizedAccessException => "PrivateAccessRequired", _ => null }) };
    }

    /// <summary>Writes one bounded trusted diagnostic to stderr; writer failures remain secondary.</summary>
    /// <param name="writer">Caller-owned diagnostic writer.</param>
    internal async Task WriteAsync(TextWriter writer)
    {
        try
        {
            var bytes = ObservedBytes.HasValue ? $" observedBytes={ObservedBytes.Value.ToString(CultureInfo.InvariantCulture)}; allowedBytes={AllowedBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"};" : string.Empty;
            await writer.WriteLineAsync($"{Code} {Operation}: {Cause}{bytes} sideEffects={SideEffects}. {Recovery} See {Documentation}.").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Optional storage output never substitutes for the primary test/gate/cancellation outcome.
        }
    }
}

/// <summary>Bounds strings and renders local paths without exposing user prefixes or control characters.</summary>
internal static class CoverageArtifactDisplay
{
    /// <summary>Renders an observed path for local diagnostics, with no credential-bearing URI support.</summary>
    /// <param name="path">A local filesystem path, never a command template.</param>
    /// <param name="repositoryRoot">Optional repository prefix to replace with a relative path.</param>
    internal static string PathLabel(string path, string? repositoryRoot = null)
    {
        var full = Path.GetFullPath(path);
        if (repositoryRoot is not null)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(repositoryRoot), full);
            if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return Bound(relative);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.IsNullOrEmpty(home) && (full.Equals(home, comparison) || full.StartsWith(home + Path.DirectorySeparatorChar, comparison)))
            full = "<home>" + full[home.Length..];
        return Bound(full);
    }

    /// <summary>Escapes controls and bounds an informational value; it never creates executable shell text.</summary>
    /// <param name="value">Local informational text.</param>
    internal static string Bound(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Take(256))
        {
            if (char.IsControl(character)) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(character);
        }
        if (value.Length > 256) builder.Append("...");
        return builder.ToString();
    }
}

/// <summary>Tracks one cumulative monotonic deadline for optional storage work.</summary>
internal sealed class CoverageArtifactWorkBudget(TimeProvider clock, TimeSpan? duration)
{
    private readonly long _started = clock.GetTimestamp();

    /// <summary>Gets elapsed wall-clock work time, including waits and filesystem operations.</summary>
    internal TimeSpan Elapsed => clock.GetElapsedTime(_started);

    /// <summary>Gets the remaining cooperative allowance, or null for explicit cancellable operations.</summary>
    internal TimeSpan? Remaining => duration.HasValue ? TimeSpan.FromTicks(Math.Max(0, duration.Value.Ticks - Elapsed.Ticks)) : null;

    /// <summary>Checks cancellation and the work deadline before the next bounded chunk.</summary>
    /// <param name="token">The original caller cancellation token.</param>
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (duration.HasValue && Elapsed >= duration.Value) throw new TimeoutException("Optional artifact work deadline reached.");
    }
}

/// <summary>Trusted failure categories for bounded store-open diagnostics; these grant no ownership.</summary>
internal enum CoverageArtifactStoreFailureKind
{
    MarkerMissing,
    MarkerInvalid,
    MarkerSchemaUnsupported,
    IdentityMismatch,
    VolumeIdentityMismatch,
    RegistryInvalid,
    PolicyInvalid,
    PrivateAccessRequired
}

/// <summary>Classifies failures without parsing exception messages or disclosing persisted identities.</summary>
/// <remarks>
/// Schema-v1 native identities bind live objects but contain no independent continuity proof.
/// A differing device/volume therefore remains a refusal, including when the file ID matches.
/// The classification is diagnostic only: it never replaces retained-handle validation or authorizes repair.
/// </remarks>
internal static class CoverageArtifactStoreFailure
{
    /// <summary>Annotates an ordinary invalid-data error, preserving the existing exception contract.</summary>
    internal static InvalidDataException Invalid(CoverageArtifactStoreFailureKind kind, Exception? inner = null)
        => Mark(inner as InvalidDataException ?? new InvalidDataException("The artifact store cannot establish valid ownership or metadata; preserve its bytes.", inner), kind);

    /// <summary>Annotates a native permission refusal while preserving its IOException contract.</summary>
    internal static IOException PrivateAccess()
        => Mark(new IOException("Artifact storage requires private current-user access."), CoverageArtifactStoreFailureKind.PrivateAccessRequired);

    /// <summary>Classifies a nonempty unmarked root without changing the existing IOException refusal contract.</summary>
    internal static IOException MarkMissing(IOException exception) => Mark(exception, CoverageArtifactStoreFailureKind.MarkerMissing);

    private static T Mark<T>(T exception, CoverageArtifactStoreFailureKind kind) where T : Exception
    {
        exception.Data[typeof(CoverageArtifactStoreFailureKind)] = kind;
        return exception;
    }

    /// <summary>Gets a trusted classification, or null for unclassified errors; messages and payloads are never inspected.</summary>
    internal static CoverageArtifactStoreFailureKind? Kind(Exception exception)
        => exception.Data[typeof(CoverageArtifactStoreFailureKind)] is CoverageArtifactStoreFailureKind kind ? kind : null;

    /// <summary>Distinguishes an observed volume change only when both complete legacy tuples parse.</summary>
    /// <remarks>Matching file IDs across volumes are insufficient. Unknown formats remain a generic identity mismatch.</remarks>
    internal static CoverageArtifactStoreFailureKind CompareIdentity(string persisted, string observed)
    {
        return TryReadIdentity(persisted, out var oldVolume) && TryReadIdentity(observed, out var newVolume) && oldVolume != newVolume
            ? CoverageArtifactStoreFailureKind.VolumeIdentityMismatch : CoverageArtifactStoreFailureKind.IdentityMismatch;
    }

    private static bool TryReadIdentity(string value, out ulong volume)
    {
        volume = 0;
        const string prefix = "FileObjectIdentity { DeviceOrVolume = ";
        const string separator = ", FileId = ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(" }", StringComparison.Ordinal)) return false;
        var split = value.IndexOf(separator, prefix.Length, StringComparison.Ordinal);
        return split >= 0
            && ulong.TryParse(value.AsSpan(prefix.Length, split - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out volume)
            && ulong.TryParse(value.AsSpan(split + separator.Length, value.Length - split - separator.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Gets actionable trusted text for a known refusal, without raw paths, metadata or native IDs.</summary>
    internal static string Cause(CoverageArtifactStoreFailureKind kind) => kind switch
    {
        CoverageArtifactStoreFailureKind.MarkerMissing => "The existing domain has no ownership marker; its contents cannot be adopted.",
        CoverageArtifactStoreFailureKind.MarkerInvalid => "The domain ownership marker is malformed or incomplete; preserve it for inspection.",
        CoverageArtifactStoreFailureKind.MarkerSchemaUnsupported => "The domain ownership marker uses an unsupported schema; use a compatible CLI without editing metadata.",
        CoverageArtifactStoreFailureKind.VolumeIdentityMismatch => "The persisted domain device/volume identity differs from the current directory. A matching file ID does not prove ownership or continuity.",
        CoverageArtifactStoreFailureKind.IdentityMismatch => "The persisted domain identity differs from the current directory; ownership and continuity are unproven.",
        CoverageArtifactStoreFailureKind.RegistryInvalid => "The domain registry is missing, malformed, unsupported or contains invalid nested facts; preserve all history and pins.",
        CoverageArtifactStoreFailureKind.PolicyInvalid => "The domain policy is malformed, unsupported or outside its bounds; no policy has been applied.",
        _ => "Private current-user ownership and permissions are required; existing access is never repaired automatically."
    };

    /// <summary>Gets the conservative schema-v1 recovery path; no operation rebinds uncertain registrations.</summary>
    internal static string Recovery(CoverageArtifactStoreFailureKind kind) => kind switch
    {
        CoverageArtifactStoreFailureKind.IdentityMismatch or CoverageArtifactStoreFailureKind.VolumeIdentityMismatch
            => "Schema-v1 has no independent continuity proof, so in-place reconciliation is refused. Preserve the entire old domain in a private backup; select a fresh private APPSURFACE_ARTIFACT_HOME for new runs. Keep old reports as historical evidence. Never rewrite the marker or registry or delete locks. See the ASCOV201 recovery guide.",
        CoverageArtifactStoreFailureKind.PrivateAccessRequired
            => "Have the owner review private permissions or ACLs. Preserve the existing domain; a fresh private APPSURFACE_ARTIFACT_HOME can isolate new runs. Never delete locks or regenerate ownership.",
        _ => "Preserve the entire domain and review the named metadata with a compatible CLI. Use a fresh private APPSURFACE_ARTIFACT_HOME for new runs if it cannot be validated. Do not reset the registry, discard pins, edit identities or delete locks."
    };
}
