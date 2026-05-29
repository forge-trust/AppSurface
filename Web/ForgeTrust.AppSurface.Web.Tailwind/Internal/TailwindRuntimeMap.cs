using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Maps build hosts to Tailwind runtime identifiers and binary names.
/// </summary>
internal static class TailwindRuntimeMap
{
    /// <summary>
    /// Resolves the Tailwind runtime identifier for the current build host.
    /// </summary>
    /// <param name="isOsPlatform">Optional platform predicate for tests; defaults to <see cref="RuntimeInformation.IsOSPlatform"/>.</param>
    /// <param name="processArchitecture">Optional architecture provider for tests; defaults to <see cref="RuntimeInformation.ProcessArchitecture"/>.</param>
    /// <returns>
    /// A supported Tailwind RID such as <c>win-x64</c>, <c>linux-arm64</c>, or <c>osx-arm64</c>; otherwise
    /// <c>unknown</c> when the platform or architecture is not mapped. This method does not throw for unsupported hosts.
    /// </returns>
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

    /// <summary>
    /// Maps an operating system and processor architecture pair to the Tailwind runtime package RID.
    /// </summary>
    /// <param name="osPlatform">The detected or test-supplied operating system platform.</param>
    /// <param name="architecture">The detected or test-supplied process architecture.</param>
    /// <returns>
    /// A supported Tailwind RID such as <c>win-x64</c>, <c>linux-x64</c>, <c>linux-arm64</c>, <c>osx-x64</c>,
    /// or <c>osx-arm64</c>; otherwise <c>unknown</c> for unsupported combinations.
    /// </returns>
    /// <remarks>
    /// Windows Arm64 intentionally maps to <c>win-x64</c> because Tailwind does not publish a Windows Arm64
    /// standalone binary for the pinned version. That relies on Windows x64 emulation; callers that cannot use
    /// emulation should provide an explicit CLI path instead of a packaged runtime.
    /// </remarks>
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

    /// <summary>
    /// Gets the packaged Tailwind binary file name for a supported runtime identifier.
    /// </summary>
    /// <param name="rid">
    /// The Tailwind runtime identifier, such as <c>win-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>,
    /// <c>linux-arm64</c>, or <c>linux-x64</c>.
    /// </param>
    /// <returns>
    /// The runtime package binary file name for <paramref name="rid"/>, or <c>null</c> when no mapping exists.
    /// Unknown RIDs do not throw so callers can emit stable diagnostics.
    /// </returns>
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

    /// <summary>
    /// Gets the conventional project-local Tailwind executable name for the current platform.
    /// </summary>
    /// <param name="isOsPlatform">
    /// Optional platform predicate for tests; defaults to <see cref="RuntimeInformation.IsOSPlatform"/>.
    /// </param>
    /// <returns>
    /// <c>tailwindcss.exe</c> on Windows and <c>tailwindcss</c> on non-Windows hosts.
    /// </returns>
    /// <remarks>
    /// This helper is used only for project-local fallback probing. Package runtime probing should use
    /// <see cref="GetRuntimeBinaryName"/> because packaged binaries include platform-specific suffixes.
    /// </remarks>
    public static string GetLocalBinaryName(Func<OSPlatform, bool>? isOsPlatform = null)
    {
        return IsPlatform(OSPlatform.Windows, isOsPlatform) ? "tailwindcss.exe" : "tailwindcss";
    }

    private static bool IsPlatform(OSPlatform platform, Func<OSPlatform, bool>? isOsPlatform)
    {
        return isOsPlatform?.Invoke(platform) ?? RuntimeInformation.IsOSPlatform(platform);
    }
}
