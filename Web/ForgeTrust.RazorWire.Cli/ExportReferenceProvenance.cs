namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes where an exporter-managed reference was found before URL normalization.
/// </summary>
/// <remarks>
/// Provenance is internal CLI metadata used to make export diagnostics and maintainer logs actionable without exposing a new
/// public API. Offsets and line numbers are best-effort source positions in the owning HTML or CSS body; they may be
/// <see langword="null"/> when the parser can identify a reference but the original source span cannot be located safely.
/// </remarks>
internal sealed record ExportReferenceProvenance(
    string Surface,
    string? ElementName,
    string? AttributeName,
    string TokenType,
    int? Offset,
    int? Line)
{
    /// <summary>
    /// Gets a compact developer-facing description such as <c>&lt;img src&gt;</c> or <c>CSS url()</c>.
    /// </summary>
    public string DisplaySource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ElementName) && !string.IsNullOrWhiteSpace(AttributeName))
            {
                return $"<{ElementName} {AttributeName}>";
            }

            if (!string.IsNullOrWhiteSpace(ElementName))
            {
                return $"<{ElementName}>";
            }

            return Surface.Equals("stylesheet", StringComparison.OrdinalIgnoreCase)
                ? $"stylesheet {TokenType}"
                : $"{Surface} {TokenType}";
        }
    }
}
