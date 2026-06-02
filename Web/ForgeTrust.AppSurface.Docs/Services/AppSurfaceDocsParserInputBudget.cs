using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Provides parser-input admission for source harvesters that need to bound bytes before decoding or parsing.
/// </summary>
/// <remarks>
/// The helper reads at most <c>maxFileSizeBytes + 1</c> bytes so oversized files can be rejected without loading the
/// whole source into memory. Seekable streams still report their full file length in diagnostics when available.
/// </remarks>
internal static class AppSurfaceDocsParserInputBudget
{
    /// <summary>
    /// Reads a source file through a positive byte budget and decodes it for parser consumption when it is within
    /// budget.
    /// </summary>
    /// <param name="filePath">Absolute or rooted file path to open for the bounded read.</param>
    /// <param name="relativePath">Repository-relative display path used in emitted diagnostics.</param>
    /// <param name="maxFileSizeBytes">Maximum number of bytes allowed before decoding; must be greater than zero.</param>
    /// <param name="configurationKey">Configuration key shown in diagnostics so operators know which limit rejected the file.</param>
    /// <param name="diagnosticCode">Diagnostic code to emit when the file exceeds <paramref name="maxFileSizeBytes"/>.</param>
    /// <param name="harvesterType">Harvester identifier recorded on emitted diagnostics.</param>
    /// <param name="sourceKindLabel">Human-readable source kind, such as C#, included in diagnostic problem text.</param>
    /// <param name="generatedSourceGuidance">Recovery guidance appended to oversized-file diagnostics.</param>
    /// <param name="cancellationToken">Token observed while reading and decoding the source.</param>
    /// <returns>
    /// A read result with <see cref="AppSurfaceDocsParserInputReadResult.Included"/> set when the source is within
    /// budget and decoded, or skipped with a warning diagnostic when the budget is exceeded.
    /// </returns>
    /// <remarks>
    /// The method throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="maxFileSizeBytes"/> is zero
    /// or negative. Files are decoded with <see cref="StreamReader"/> using UTF-8 as the default encoding and BOM
    /// detection enabled, so a recognized BOM is consumed instead of becoming source text. Oversized diagnostics use
    /// <paramref name="diagnosticCode"/>, <paramref name="configurationKey"/>, <paramref name="harvesterType"/>,
    /// <paramref name="sourceKindLabel"/>, and <paramref name="generatedSourceGuidance"/> to describe the skipped
    /// file and recovery path.
    /// </remarks>
    internal static async Task<AppSurfaceDocsParserInputReadResult> ReadUtf8SourceAsync(
        string filePath,
        string relativePath,
        long maxFileSizeBytes,
        string configurationKey,
        string diagnosticCode,
        string harvesterType,
        string sourceKindLabel,
        string generatedSourceGuidance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(configurationKey);
        ArgumentNullException.ThrowIfNull(diagnosticCode);
        ArgumentNullException.ThrowIfNull(harvesterType);
        ArgumentNullException.ThrowIfNull(sourceKindLabel);
        ArgumentNullException.ThrowIfNull(generatedSourceGuidance);

        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), maxFileSizeBytes, "The parser input byte limit must be positive.");
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var readLimit = maxFileSizeBytes == long.MaxValue ? long.MaxValue : maxFileSizeBytes + 1;
        var bufferSize = (int)Math.Min(16 * 1024, Math.Max(1L, readLimit));
        var buffer = new byte[bufferSize];
        var initialCapacity = (int)Math.Min(Math.Min(stream.Length, maxFileSizeBytes), bufferSize);
        using var output = new MemoryStream(capacity: initialCapacity);
        long totalBytesRead = 0;

        while (true)
        {
            var remainingBudget = readLimit - totalBytesRead;
            var readLength = (int)Math.Min(buffer.Length, remainingBudget);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
            if (totalBytesRead > maxFileSizeBytes)
            {
                return AppSurfaceDocsParserInputReadResult.Skipped(
                    CreateFileTooLargeDiagnostic(
                        diagnosticCode,
                        harvesterType,
                        relativePath,
                        stream.Length,
                        maxFileSizeBytes,
                        configurationKey,
                        sourceKindLabel,
                        generatedSourceGuidance));
            }

            output.Write(buffer, 0, bytesRead);
        }

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var source = await reader.ReadToEndAsync(cancellationToken);
        return AppSurfaceDocsParserInputReadResult.Read(source);
    }

    /// <summary>
    /// Creates the warning diagnostic emitted when a parser-input budget rejects a source file.
    /// </summary>
    /// <param name="diagnosticCode">Diagnostic code that identifies the source-specific oversized-file condition.</param>
    /// <param name="harvesterType">Harvester identifier attached to the diagnostic.</param>
    /// <param name="relativePath">Repository-relative source path shown to operators.</param>
    /// <param name="observedSizeBytes">Observed byte size, preferably the full file length when the stream is seekable.</param>
    /// <param name="maxFileSizeBytes">Configured positive byte limit that rejected the file.</param>
    /// <param name="configurationKey">Configuration key that controls <paramref name="maxFileSizeBytes"/>.</param>
    /// <param name="sourceKindLabel">Human-readable source kind included in the diagnostic problem.</param>
    /// <param name="generatedSourceGuidance">Recovery guidance for generated or intentionally large source.</param>
    /// <returns>A non-strict warning diagnostic suitable for harvest health output.</returns>
    private static DocHarvestDiagnostic CreateFileTooLargeDiagnostic(
        string diagnosticCode,
        string harvesterType,
        string relativePath,
        long observedSizeBytes,
        long maxFileSizeBytes,
        string configurationKey,
        string sourceKindLabel,
        string generatedSourceGuidance)
    {
        var observedSize = observedSizeBytes.ToString(CultureInfo.InvariantCulture);
        var configuredSize = maxFileSizeBytes.ToString(CultureInfo.InvariantCulture);

        return new DocHarvestDiagnostic(
            diagnosticCode,
            DocHarvestDiagnosticSeverity.Warning,
            harvesterType,
            $"Skipped {sourceKindLabel} file '{relativePath}' because it is larger than the configured parser input limit.",
            $"The observed source size is {observedSize} bytes and {configurationKey} is {configuredSize}.",
            $"{generatedSourceGuidance} See the AppSurface Docs oversized source diagnostic guidance for {diagnosticCode}.");
    }
}

/// <summary>
/// Represents the outcome of a bounded parser-input read.
/// </summary>
/// <param name="Included">
/// <c>true</c> when the source was within budget and <paramref name="Source"/> contains decoded text; <c>false</c>
/// when the source was skipped and <paramref name="Diagnostic"/> explains why.
/// </param>
/// <param name="Source">Decoded source text for included files, otherwise <c>null</c>.</param>
/// <param name="Diagnostic">Warning diagnostic for skipped files, otherwise <c>null</c>.</param>
/// <remarks>
/// Callers should branch on <see cref="Included"/> before using <see cref="Source"/>. Skipped results are intended to
/// let harvesters continue processing sibling files while surfacing a visible diagnostic.
/// </remarks>
internal sealed record AppSurfaceDocsParserInputReadResult(
    bool Included,
    string? Source,
    DocHarvestDiagnostic? Diagnostic)
{
    /// <summary>
    /// Creates an included result for decoded source that stayed within the byte budget.
    /// </summary>
    /// <param name="source">Decoded source text ready for parser consumption.</param>
    /// <returns>An included parser-input result with no diagnostic.</returns>
    public static AppSurfaceDocsParserInputReadResult Read(string source)
    {
        return new AppSurfaceDocsParserInputReadResult(true, source, null);
    }

    /// <summary>
    /// Creates a skipped result for a source file rejected before decoding or parsing.
    /// </summary>
    /// <param name="diagnostic">Diagnostic that explains why the source was skipped and how to recover.</param>
    /// <returns>A skipped parser-input result with no decoded source.</returns>
    public static AppSurfaceDocsParserInputReadResult Skipped(DocHarvestDiagnostic diagnostic)
    {
        return new AppSurfaceDocsParserInputReadResult(false, null, diagnostic);
    }
}
