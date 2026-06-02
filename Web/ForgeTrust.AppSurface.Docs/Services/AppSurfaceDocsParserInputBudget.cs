using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

internal static class AppSurfaceDocsParserInputBudget
{
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
        var initialCapacity = stream.CanSeek
            ? (int)Math.Min(Math.Min(stream.Length, maxFileSizeBytes), bufferSize)
            : 0;
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
                        totalBytesRead,
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
            $"The harvester read {observedSize} bytes before decoding and {configurationKey} is {configuredSize}.",
            $"{generatedSourceGuidance} See the AppSurface Docs oversized source diagnostic guidance for {diagnosticCode}.");
    }
}

internal sealed record AppSurfaceDocsParserInputReadResult(
    bool Included,
    string? Source,
    DocHarvestDiagnostic? Diagnostic)
{
    public static AppSurfaceDocsParserInputReadResult Read(string source)
    {
        return new AppSurfaceDocsParserInputReadResult(true, source, null);
    }

    public static AppSurfaceDocsParserInputReadResult Skipped(DocHarvestDiagnostic diagnostic)
    {
        return new AppSurfaceDocsParserInputReadResult(false, null, diagnostic);
    }
}
