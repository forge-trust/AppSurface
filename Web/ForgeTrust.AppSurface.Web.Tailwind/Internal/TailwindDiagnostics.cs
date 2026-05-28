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

    public const string UnsupportedRid = "ASTW001";
    public const string MissingVersion = "ASTW002";
    public const string InvalidCliPath = "ASTW003";
    public const string MissingCli = "ASTW004";
    public const string ProcessStartFailed = "ASTW005";
    public const string NonZeroExit = "ASTW006";
    public const string Canceled = "ASTW007";
    public const string SameInputOutput = "ASTW008";

    public static string Format(string code, string problem, string cause, string fix)
    {
        return $"{code}: {problem} Cause: {cause} Fix: {fix} See: {HelpUrl}";
    }
}
