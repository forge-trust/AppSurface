using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Parses complete joined worker bytes for the fixed private N05 image; grants no native authority.</summary>
/// <remarks>Only the actual retained worker's custody guard may precede production use. Test receipts are data.</remarks>
internal static class N05JoinedWorkerOutput
{
    /// <summary>Maximum complete stderr bytes, comprising two lines each bounded to 1024 bytes including LF.</summary>
    internal const int MaximumBytes = 2048;
    /// <summary>Exact source-selected terminal text; neither arbitrary messages nor prefixes are accepted.</summary>
    internal const string TerminalLine = "ASEVD409: Fresh output allocation or activation failed. Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    { "schema", "phase", "operation", "stageOutcome", "terminalCode", "errorClass", "nativeErrno" };

    /// <summary>Detached worker-reported observations, never a root syscall receipt or proof of activation.</summary>
    /// <param name="WorkerReportedErrno">Actual decoded bounded worker report, or null; never inferred.</param>
    /// <param name="StderrBytes">Complete received stderr count from joined pump observations.</param>
    internal sealed record Projection(int? WorkerReportedErrno, int StderrBytes);

    /// <summary>Requires two successful EOFs, full retention, empty stdout and the exact closed allocation/terminal pair.</summary>
    /// <param name="output">Immutable actual joined output, or data-only receipts in intentionally portable tests.</param>
    /// <param name="token">Original teardown token; no timeout or cancellation source is created.</param>
    /// <returns>Only detached fixed-case data. It cannot authenticate a process, root peer or allocation.</returns>
    /// <exception cref="InvalidOperationException">Fixed rejection without input, inner exception or arbitrary output.</exception>
    internal static Projection Parse(SupervisionOutputReceipt? output, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (output is null || output.Stdout is null || output.Stderr is null
                || output.Stdout.Prefix.IsDefault || output.Stderr.Prefix.IsDefault
                || !output.Successful || output.ReceivedByteLimit <= 0
                || output.ReceivedByteLimit > EvidenceRunBudgetLimits.MaximumProcessOutputBytes
                || output.Stdout.ReceivedBytes != 0 || output.Stdout.Prefix.Length != 0
                || output.Stderr.ReceivedBytes <= 0 || output.Stderr.ReceivedBytes > MaximumBytes
                || output.Stderr.ReceivedBytes != output.Stderr.Prefix.Length
                || output.ReceivedBytes != output.Stderr.ReceivedBytes) throw Rejected();
            var bytes = output.Stderr.Prefix.AsSpan(); // Count checked before copying/decoding.
            if (bytes[^1] != (byte)'\n') throw Rejected();
            var text = StrictUtf8.GetString(bytes);
            var lines = text.Split('\n');
            if (lines.Length != 3 || lines[2].Length != 0 || lines[1] != TerminalLine
                || StrictUtf8.GetByteCount(lines[0]) + 1 > 1024
                || StrictUtf8.GetByteCount(lines[1]) + 1 > 1024) throw Rejected();
            using var document = JsonDocument.Parse(lines[0], new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Rejected();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Keys.Contains(field.Name) || !seen.Add(field.Name)) throw Rejected();
            if (!seen.SetEquals(Keys)) throw Rejected();
            RequireString(root, "schema", "evidence-allocation-failure-v1");
            RequireString(root, "phase", "Allocation");
            RequireString(root, "operation", "CreateSlot");
            RequireString(root, "stageOutcome", "Failed");
            RequireString(root, "terminalCode", "StageFailed");
            RequireString(root, "errorClass", "Io");
            var errno = root.GetProperty("nativeErrno");
            int? value = null;
            if (errno.ValueKind != JsonValueKind.Null)
            {
                if (errno.ValueKind != JsonValueKind.Number || !errno.TryGetInt32(out var number)
                    || number is < 1 or > 4095) throw Rejected();
                value = number;
            }
            token.ThrowIfCancellationRequested();
            return new(value, checked((int)output.Stderr.ReceivedBytes));
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException
            or InvalidOperationException or ArgumentException or OverflowException)
        { throw Rejected(); }
    }

    /// <summary>Serializes only closed worker-reported values, at most 1024 UTF-8 bytes including the caller's LF.</summary>
    /// <remarks>Requires the original token. The native caller rechecks custody/files around actual write and flush.</remarks>
    /// <param name="value">Detached parser data; constructing it in tests creates no execution capability.</param>
    /// <param name="token">Original cleanup token, never renewed.</param>
    /// <returns>Fixed private JSON with native authority explicitly false and no raw output.</returns>
    internal static string Serialize(Projection value, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (value is null || value.StderrBytes is < 1 or > MaximumBytes
            || value.WorkerReportedErrno is < 1 or > 4095) throw Rejected();
        var line = JsonSerializer.Serialize(new
        {
            schema = "issue779-n05-joined-worker-output-v1", origin = "joined-worker-output",
            allocation = new { schema = "evidence-allocation-failure-v1", phase = "Allocation",
                operation = "CreateSlot", stageOutcome = "Failed", terminalCode = "StageFailed",
                errorClass = "Io", nativeErrno = value.WorkerReportedErrno },
            terminal_diagnostic = "ASEVD409", stdout_bytes = 0, stderr_bytes = value.StderrBytes,
            native_authority = false,
        });
        if (StrictUtf8.GetByteCount(line) + 1 > 1024) throw Rejected();
        token.ThrowIfCancellationRequested();
        return line;
    }

    private static void RequireString(JsonElement root, string name, string expected)
    {
        var field = root.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String || field.GetString() != expected) throw Rejected();
    }

    private static InvalidOperationException Rejected() => new("ASEVD410: N05 joined output diagnostic rejected.");
}
