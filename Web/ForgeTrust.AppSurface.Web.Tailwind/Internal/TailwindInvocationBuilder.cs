using System.Runtime.InteropServices;

namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Builds process invocations for Tailwind CLI binaries and Windows shell shims.
/// </summary>
/// <remarks>
/// npm-installed Tailwind commands on Windows may be <c>.cmd</c>, <c>.bat</c>, or <c>.ps1</c> wrapper scripts
/// instead of native executables. Use this builder after resolving a concrete Tailwind path so callers get the
/// platform-specific launcher and argument order without duplicating shell rules. Pass unquoted paths and
/// arguments; CliWrap performs process argument escaping when the invocation is executed.
/// </remarks>
internal static class TailwindInvocationBuilder
{
    /// <summary>
    /// Creates the process filename and argument list needed to launch Tailwind on the current platform.
    /// </summary>
    /// <param name="tailwindPath">
    /// The resolved Tailwind executable or known Windows shim path. The value must be non-empty and should not be quoted.
    /// </param>
    /// <param name="tailwindArgs">The ordered Tailwind arguments to append after any shell-wrapper arguments.</param>
    /// <param name="isOsPlatform">Optional platform predicate used by tests; defaults to <see cref="RuntimeInformation.IsOSPlatform"/>.</param>
    /// <returns>
    /// A <see cref="TailwindProcessInvocation"/> that invokes binaries directly on Unix-like platforms and wraps
    /// <c>.cmd</c>/<c>.bat</c>/<c>.ps1</c> scripts on Windows.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="tailwindPath"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tailwindPath"/> or <paramref name="tailwindArgs"/> is <c>null</c>.</exception>
    public static TailwindProcessInvocation Build(
        string tailwindPath,
        IReadOnlyList<string> tailwindArgs,
        Func<OSPlatform, bool>? isOsPlatform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tailwindPath);
        ArgumentNullException.ThrowIfNull(tailwindArgs);

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
            // npm PowerShell shims are often unsigned. Keep this branch limited to resolved Tailwind paths, because
            // ExecutionPolicy Bypass would otherwise make untrusted scripts easier to launch in build environments.
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
