namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Describes one CDN export validation problem with a stable code and actionable context.
/// </summary>
internal sealed record ExportDiagnostic(
    string Code,
    string Message,
    string Route,
    ExportReference? Reference = null);
