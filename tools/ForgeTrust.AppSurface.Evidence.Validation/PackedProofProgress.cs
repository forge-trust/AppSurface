using System.Diagnostics;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Writes diagnostic boundaries as they happen so they survive a watchdog stopping the test host.</summary>
/// <remarks>One proof owns each file. A final incomplete line can be ignored. Diagnostics carry no acceptance authority.</remarks>
internal sealed class PackedProofProgress(FileStream stream) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private FileStream? _stream = stream;

    /// <summary>Creates a fresh, readable journal without replacing an existing file.</summary>
    /// <returns>The owned diagnostic writer, or null when the journal cannot be opened.</returns>
    internal static PackedProofProgress? TryCreate(string path)
    {
        try { return new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Flushes one boundary with a fixed stage label and optional observed exit code.</summary>
    /// <remarks>A closed stream disables further diagnostics, including when disposal interrupts a write.</remarks>
    internal void Record(string stage, string boundary, int? exitCode = null)
    {
        if (Volatile.Read(ref _stream) is not { } log) return;
        try
        {
            log.Write(JsonSerializer.SerializeToUtf8Bytes(new
            {
                stage,
                boundary,
                observedAtUtc = DateTimeOffset.UtcNow,
                elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                exitCode,
            }));
            log.WriteByte((byte)'\n');
            log.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Dispose();
        }
    }

    /// <summary>Closes the journal without changing the proof result if diagnostics fail.</summary>
    public void Dispose()
    {
        var log = Interlocked.Exchange(ref _stream, null);
        try { log?.Dispose(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
