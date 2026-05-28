using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Builds process invocations for Tailwind CLI binaries and shell shims.
/// </summary>
internal static class TailwindInvocationBuilder
{
    public static TailwindProcessInvocation Build(
        string tailwindPath,
        IReadOnlyList<string> tailwindArgs,
        Func<OSPlatform, bool>? isOsPlatform = null)
    {
        if (!IsWindows(isOsPlatform))
        {
            return new TailwindProcessInvocation(tailwindPath, tailwindArgs);
        }

        var extension = Path.GetExtension(tailwindPath);
        if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            return new TailwindProcessInvocation(
                "cmd.exe",
                CreateInvocationArguments("/d", "/c", tailwindPath, tailwindArgs));
        }

        if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return new TailwindProcessInvocation(
                "powershell.exe",
                CreateInvocationArguments("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", tailwindPath, tailwindArgs));
        }

        return new TailwindProcessInvocation(tailwindPath, tailwindArgs);
    }

    private static bool IsWindows(Func<OSPlatform, bool>? isOsPlatform)
    {
        return isOsPlatform?.Invoke(OSPlatform.Windows) ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    private static IReadOnlyList<string> CreateInvocationArguments(
        string firstArg,
        string secondArg,
        string thirdArg,
        IReadOnlyList<string> tailwindArgs)
    {
        var arguments = new List<string>(tailwindArgs.Count + 3)
        {
            firstArg,
            secondArg,
            thirdArg
        };
        arguments.AddRange(tailwindArgs);
        return arguments;
    }

    private static IReadOnlyList<string> CreateInvocationArguments(
        string firstArg,
        string secondArg,
        string thirdArg,
        string fourthArg,
        string fifthArg,
        string sixthArg,
        IReadOnlyList<string> tailwindArgs)
    {
        var arguments = new List<string>(tailwindArgs.Count + 6)
        {
            firstArg,
            secondArg,
            thirdArg,
            fourthArg,
            fifthArg,
            sixthArg
        };
        arguments.AddRange(tailwindArgs);
        return arguments;
    }
}
