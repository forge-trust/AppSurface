namespace ForgeTrust.AppSurface.Web.Tailwind.Internal;

/// <summary>
/// Defines stable Tailwind build diagnostic codes and help text.
/// </summary>
internal static class TailwindDiagnostics
{
    /// <summary>
    /// Documentation anchor included in emitted build diagnostics.
    /// </summary>
    public const string HelpUrl =
        "https://github.com/forge-trust/AppSurface/tree/main/Web/ForgeTrust.AppSurface.Web.Tailwind#tailwind-diagnostics";

    /// <summary>
    /// Diagnostic emitted when the build host operating system or architecture has no packaged Tailwind runtime.
    /// </summary>
    public const string UnsupportedRid = "ASTW001";

    /// <summary>
    /// Diagnostic emitted when <c>TailwindVersion</c> is missing while build mode is resolving a packaged runtime.
    /// </summary>
    public const string MissingVersion = "ASTW002";

    /// <summary>
    /// Diagnostic emitted when an explicit <c>TailwindCliPath</c> points to a file that does not exist.
    /// </summary>
    public const string InvalidCliPath = "ASTW003";

    /// <summary>
    /// Diagnostic emitted when no explicit, packaged, project-local, or source-tree Tailwind executable exists.
    /// </summary>
    public const string MissingCli = "ASTW004";

    /// <summary>
    /// Diagnostic emitted when the resolved Tailwind executable exists but the operating system cannot start it.
    /// </summary>
    public const string ProcessStartFailed = "ASTW005";

    /// <summary>
    /// Diagnostic emitted when the Tailwind process starts successfully and exits with a non-zero exit code.
    /// </summary>
    public const string NonZeroExit = "ASTW006";

    /// <summary>
    /// Diagnostic emitted when MSBuild cancels the task before the Tailwind process finishes.
    /// </summary>
    public const string Canceled = "ASTW007";

    /// <summary>
    /// Diagnostic emitted when Tailwind input and output paths resolve to the same file.
    /// </summary>
    public const string SameInputOutput = "ASTW008";

    /// <summary>
    /// Formats a stable Tailwind diagnostic message.
    /// </summary>
    /// <param name="code">The <c>ASTW###</c> diagnostic code.</param>
    /// <param name="problem">A short description of what failed.</param>
    /// <param name="cause">The likely cause to show after the <c>Cause:</c> label.</param>
    /// <param name="fix">The recommended action to show after the <c>Fix:</c> label.</param>
    /// <returns>
    /// A single-line diagnostic containing the code, problem, cause, fix, and <see cref="HelpUrl"/> reference.
    /// Inputs are expected to be non-empty caller-supplied message fragments.
    /// </returns>
    public static string Format(string code, string problem, string cause, string fix)
    {
        return $"{code}: {problem} Cause: {cause} Fix: {fix} See: {HelpUrl}";
    }
}
