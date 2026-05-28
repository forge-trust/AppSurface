using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Maps build hosts to Tailwind runtime identifiers and binary names.
/// </summary>
internal static class TailwindRuntimeMap
{
    public static string GetCurrentRid(
        Func<OSPlatform, bool>? isOsPlatform = null,
        Func<Architecture>? processArchitecture = null)
    {
        var architecture = processArchitecture?.Invoke() ?? RuntimeInformation.ProcessArchitecture;

        if (IsPlatform(OSPlatform.Windows, isOsPlatform))
        {
            return ResolveRid(OSPlatform.Windows, architecture);
        }

        if (IsPlatform(OSPlatform.Linux, isOsPlatform))
        {
            return ResolveRid(OSPlatform.Linux, architecture);
        }

        if (IsPlatform(OSPlatform.OSX, isOsPlatform))
        {
            return ResolveRid(OSPlatform.OSX, architecture);
        }

        return "unknown";
    }

    public static string ResolveRid(OSPlatform osPlatform, Architecture architecture)
    {
        if (osPlatform == OSPlatform.Windows)
        {
            return architecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-x64",
                _ => "unknown"
            };
        }

        if (osPlatform == OSPlatform.Linux)
        {
            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => "unknown"
            };
        }

        if (osPlatform == OSPlatform.OSX)
        {
            return architecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => "unknown"
            };
        }

        return "unknown";
    }

    public static string? GetRuntimeBinaryName(string rid)
    {
        return rid switch
        {
            "win-x64" => "tailwindcss-windows-x64.exe",
            "osx-arm64" => "tailwindcss-macos-arm64",
            "osx-x64" => "tailwindcss-macos-x64",
            "linux-arm64" => "tailwindcss-linux-arm64",
            "linux-x64" => "tailwindcss-linux-x64",
            _ => null
        };
    }

    public static string GetLocalBinaryName(Func<OSPlatform, bool>? isOsPlatform = null)
    {
        return IsPlatform(OSPlatform.Windows, isOsPlatform) ? "tailwindcss.exe" : "tailwindcss";
    }

    private static bool IsPlatform(OSPlatform platform, Func<OSPlatform, bool>? isOsPlatform)
    {
        return isOsPlatform?.Invoke(platform) ?? RuntimeInformation.IsOSPlatform(platform);
    }
}
