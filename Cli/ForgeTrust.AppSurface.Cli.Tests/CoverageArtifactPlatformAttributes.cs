namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Runs native Windows assertions only where their APIs are available.</summary>
public sealed class CoverageWindowsFactAttribute : FactAttribute
{
    public CoverageWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows file ownership and ACL APIs.";
    }
}

/// <summary>Reports Unix-only assertions as discovery-time skips on other hosts.</summary>
public sealed class CoverageUnixFactAttribute : FactAttribute
{
    public CoverageUnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Requires native Linux or macOS filesystem APIs.";
    }
}

/// <summary>
/// Runs Unix permission-denial assertions only when mode bits are enforced for this identity.
/// The temporary-file probe runs during test discovery; setup and unexpected read failures are errors,
/// while only a successful read causes the test to be skipped.
/// </summary>
public sealed class CoverageUnixPermissionFactAttribute : FactAttribute
{
    /// <summary>Checks the current identity's mode-000 file behavior and reports an unsupported case as skipped.</summary>
    public CoverageUnixPermissionFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "Requires Unix file-mode enforcement.";
            return;
        }

        if (!CoverageUnixPermissionCapability.IsModeZeroUnreadable())
            Skip = "The current test identity can read files with mode 000.";
    }
}

internal static class CoverageUnixPermissionCapability
{
    /// <summary>Returns false only when a mode-000 file remains readable; setup and unexpected read errors propagate.</summary>
    public static bool IsModeZeroUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return false;

        var path = Path.Combine(Path.GetTempPath(), $"coverage-permission-{Guid.NewGuid():N}.tmp");
        var modeChanged = false;
        try
        {
            File.WriteAllText(path, "probe");
            // Only a successfully opened mode-000 file is evidence that this identity bypasses the restriction.
            File.SetUnixFileMode(path, UnixFileMode.None);
            modeChanged = true;
            try
            {
                using var stream = File.OpenRead(path);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        finally
        {
            if (modeChanged)
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
        }
    }
}

/// <summary>Reports Unix-only theory cases as discovery-time skips on other hosts.</summary>
public sealed class CoverageUnixTheoryAttribute : TheoryAttribute
{
    public CoverageUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Requires native Linux or macOS filesystem APIs.";
    }
}
