namespace ForgeTrust.RazorWire.Cli;

internal enum ExportSourceKind
{
    Url,
    Project,
    Dll
}

internal sealed record ExportSourceRequest(
    ExportSourceKind SourceKind,
    string SourceValue,
    string? Framework,
    IReadOnlyList<string> AppArgs,
    bool NoBuild);
