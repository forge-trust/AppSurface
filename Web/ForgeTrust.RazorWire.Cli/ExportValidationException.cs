namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Represents exporter-domain validation failures that prevent safe export output from being produced.
/// </summary>
/// <remarks>
/// The export engine throws this exception without depending on CLI infrastructure. Command handlers should translate
/// it into the appropriate command-line failure type for their host.
/// </remarks>
public sealed class ExportValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="ExportValidationException"/>.
    /// </summary>
    /// <param name="diagnostics">The validation diagnostics that describe why the export is unsafe.</param>
    public ExportValidationException(IReadOnlyList<ExportDiagnostic> diagnostics)
        : base(CreateMessage(ValidateDiagnostics(diagnostics)))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the diagnostics that describe why export validation failed.
    /// </summary>
    public IReadOnlyList<ExportDiagnostic> Diagnostics { get; }

    private static IReadOnlyList<ExportDiagnostic> ValidateDiagnostics(IReadOnlyList<ExportDiagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics;
    }

    private static string CreateMessage(IReadOnlyList<ExportDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "Export validation failed.";
        }

        return "Export validation failed:" + Environment.NewLine
               + string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}"));
    }
}
