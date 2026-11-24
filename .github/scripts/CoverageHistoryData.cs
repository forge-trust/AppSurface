using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

/// <summary>
/// Validates bounded coverage timing JSON and root timing entries from ZIP artifacts using SharpCompress's
/// ZIP-specific parser and Python's supported compression methods.
/// </summary>
internal static class CoverageHistoryData
{
    /// <summary>Maximum size of an API JSON response or uncompressed archive entry.</summary>
    internal const int MaxJsonBytes = 2 * 1024 * 1024;

    /// <summary>Maximum size of a downloaded archive, including its ZIP overhead.</summary>
    internal const int MaxArchiveBytes = MaxJsonBytes + 64 * 1024;
    private const int MaxProjects = 1000;
    private const int MaxProjectPathCodePoints = 4096;
    private const int MaxJsonDepth = 1000;

    /// <summary>
    /// Parses bounded UTF-8 JSON with unique property names at every depth. Numeric ranges apply
    /// when a field is consumed. The caller owns the returned document.
    /// </summary>
    internal static JsonDocument ReadJson(byte[] raw)
    {
        if (raw is null)
        {
            throw new InvalidDataException("JSON data is missing.");
        }

        if (raw.Length > MaxJsonBytes)
        {
            throw new InvalidDataException("JSON exceeds the 2 MiB limit.");
        }

        if (!Utf8.IsValid(raw))
        {
            throw new InvalidDataException("JSON must be valid UTF-8.");
        }

        try
        {
            return JsonDocument.Parse(raw.AsMemory(), new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                MaxDepth = MaxJsonDepth
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Invalid JSON.", exception);
        }
    }

    /// <summary>
    /// Reads exactly one non-directory, root-level <c>timings.json</c> entry without extracting files.
    /// The ZIP reader accepts stored, deflate, BZIP2, and LZMA entries, validates the selected entry's CRC
    /// and timing schema, then returns the entry's original bytes.
    /// </summary>
    internal static byte[] ReadArchive(byte[] raw)
    {
        if (raw is null)
        {
            throw new InvalidDataException("ZIP archive is missing.");
        }

        if (raw.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException("ZIP archive exceeds its size limit.");
        }

        try
        {
            using var archiveStream = new MemoryStream(raw, writable: false);
            using var archive = ZipArchive.OpenArchive(
                archiveStream,
                new ReaderOptions { LeaveStreamOpen = true });
            IArchiveEntry? timingEntry = null;

            foreach (var entry in archive.Entries)
            {
                if (entry.Size < 0 || entry.Size > MaxJsonBytes)
                {
                    throw new InvalidDataException("ZIP entry exceeds the 2 MiB limit.");
                }

                if (entry.IsDirectory ||
                    !string.Equals(entry.Key, "timings.json", StringComparison.Ordinal))
                {
                    continue;
                }

                if (timingEntry is not null)
                {
                    throw new InvalidDataException("ZIP must contain exactly one root timings.json.");
                }

                timingEntry = entry;
            }

            if (timingEntry is null)
            {
                throw new InvalidDataException("ZIP must contain exactly one root timings.json.");
            }

            if (timingEntry.CompressionType is not (
                CompressionType.None or CompressionType.Deflate or CompressionType.BZip2 or CompressionType.LZMA))
            {
                throw new InvalidDataException("timings.json uses a ZIP compression method Python does not support.");
            }

            if (timingEntry.IsEncrypted)
            {
                throw new InvalidDataException("Encrypted ZIP entries are not supported.");
            }

            var contents = ReadEntry(timingEntry);
            if (Crc32(contents) != unchecked((uint)timingEntry.Crc))
            {
                throw new InvalidDataException("ZIP timings.json CRC does not match.");
            }

            using var document = ReadJson(contents);
            ValidateTimings(document.RootElement);
            return contents;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (SharpCompressException exception)
        {
            throw new InvalidDataException("Invalid ZIP archive or timings entry.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("Invalid ZIP archive or timings entry.", exception);
        }
    }

    private static byte[] ReadEntry(IArchiveEntry entry)
    {
        using var entryStream = entry.OpenEntryStream();
        var contents = new byte[checked((int)entry.Size)];
        entryStream.ReadExactly(contents);
        if (entryStream.ReadByte() != -1)
        {
            throw new InvalidDataException("timings.json exceeds its declared size.");
        }

        return contents;
    }

    private static void ValidateTimings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("projects", out var projects) ||
            projects.ValueKind != JsonValueKind.Array ||
            projects.GetArrayLength() is < 1 or > MaxProjects)
        {
            throw new InvalidDataException("projects must be a nonempty array of at most 1000 entries.");
        }

        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectTiming in projects.EnumerateArray())
        {
            if (projectTiming.ValueKind != JsonValueKind.Object ||
                !projectTiming.TryGetProperty("project", out var projectValue) ||
                projectValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("project must be a safe relative .csproj path.");
            }

            string project;
            try
            {
                project = projectValue.GetString()!;
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException("project must contain valid Unicode.", exception);
            }

            if (!IsSafeProjectPath(project))
            {
                throw new InvalidDataException("project must be a safe relative .csproj path.");
            }

            if (!seenProjects.Add(project))
            {
                throw new InvalidDataException("Duplicate project path ignoring case.");
            }

            if (!projectTiming.TryGetProperty("seconds", out var secondsValue) ||
                secondsValue.ValueKind != JsonValueKind.Number ||
                !secondsValue.TryGetInt32(out var seconds) ||
                seconds < 0)
            {
                throw new InvalidDataException("seconds must be a bounded nonnegative integer.");
            }
        }
    }

    private static bool IsSafeProjectPath(string project)
    {
        if (project.Length == 0 || project.EnumerateRunes().Count() > MaxProjectPathCodePoints ||
            !string.Equals(project, project.Trim(), StringComparison.Ordinal) ||
            project.Contains('\\') || project.Contains(':') ||
            project.StartsWith("/", StringComparison.Ordinal) ||
            !project.EndsWith(".csproj", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in project)
        {
            if (character < 0x20 || character == 0x7f)
            {
                return false;
            }
        }

        var segmentStart = 0;
        for (var index = 0; index <= project.Length; index++)
        {
            if (index != project.Length && project[index] != '/')
            {
                continue;
            }

            var segment = project.AsSpan(segmentStart, index - segmentStart);
            if (segment.IsEmpty || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                return false;
            }

            segmentStart = index + 1;
        }

        return true;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            }
        }

        return ~crc;
    }
}
