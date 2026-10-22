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

/// <summary>Reports Unix-only theory cases as discovery-time skips on other hosts.</summary>
public sealed class CoverageUnixTheoryAttribute : TheoryAttribute
{
    public CoverageUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "Requires native Linux or macOS filesystem APIs.";
    }
}

