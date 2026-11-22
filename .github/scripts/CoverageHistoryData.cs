using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private const int MaxArchiveBytes = MaxJsonBytes + 64 * 1024;
    private const int MaxProjects = 1000;
    private const int MaxProjectPathCodePoints = 4096;
    private const int MaxPythonIntegerDigits = 4300;
    private const int MaxJsonDepth = 1000;

    private static readonly Dictionary<int, string> FullCaseFolds = CreateFullCaseFolds();

    /// <summary>
    /// Parses strict UTF-8 JSON within the artifact size and nesting limits. Duplicate properties at any
    /// depth and integer tokens beyond Python's default decimal digit limit are rejected. The caller owns
    /// and must dispose the returned document.
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

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                raw.AsMemory(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxJsonDepth
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid JSON.", exception);
        }

        try
        {
            ValidateJsonTokens(raw);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
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
        using var contents = new MemoryStream((int)entry.Size);
        var buffer = new byte[8192];
        var totalBytes = 0;

        while (true)
        {
            var bytesToRead = Math.Min(buffer.Length, MaxJsonBytes + 1 - totalBytes);
            if (bytesToRead <= 0)
            {
                throw new InvalidDataException("timings.json exceeds the 2 MiB limit.");
            }

            var bytesRead = entryStream.Read(buffer, 0, bytesToRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxJsonBytes)
            {
                throw new InvalidDataException("timings.json exceeds the 2 MiB limit.");
            }

            contents.Write(buffer, 0, bytesRead);
        }

        if (totalBytes != entry.Size)
        {
            throw new InvalidDataException("timings.json is incomplete.");
        }

        return contents.ToArray();
    }

    private static void ValidateJsonTokens(byte[] raw)
    {
        var reader = new Utf8JsonReader(
            raw,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaxJsonDepth
            });
        var objectProperties = new Stack<HashSet<string>?>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    objectProperties.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    string name;
                    try
                    {
                        name = reader.GetString()!;
                    }
                    catch (InvalidOperationException)
                    {
                        name = DecodeJsonString(reader.ValueSpan);
                    }

                    if (!objectProperties.Peek()!.Add(name))
                    {
                        throw new InvalidDataException("JSON contains a duplicate object property.");
                    }
                    break;
                case JsonTokenType.Number:
                    ValidateIntegerDigitCount(Encoding.UTF8.GetString(reader.ValueSpan));
                    break;
            }
        }
    }

    private static string ReadProjectString(JsonElement value)
    {
        try
        {
            return value.GetString() ?? throw new InvalidDataException("JSON string is missing.");
        }
        catch (InvalidOperationException)
        {
            return DecodeJsonString(Encoding.UTF8.GetBytes(value.GetRawText()));
        }
    }

    private static string DecodeJsonString(ReadOnlySpan<byte> rawValue)
    {
        var value = rawValue;
        if (value.Length >= 2 && value[0] == (byte)'"' && value[^1] == (byte)'"')
        {
            value = value[1..^1];
        }

        // Utf8JsonReader has already validated the JSON escape alphabet. Regex.Unescape supports those
        // escapes and preserves unpaired UTF-16 surrogate code units from \uXXXX sequences.
        return Regex.Unescape(Encoding.UTF8.GetString(value));
    }

    private static void ValidateIntegerDigitCount(string number)
    {
        if (number.IndexOf('.') >= 0 || number.IndexOf('e') >= 0 || number.IndexOf('E') >= 0)
        {
            return;
        }

        var digitCount = number.Length - (number.StartsWith("-", StringComparison.Ordinal) ? 1 : 0);
        if (digitCount > MaxPythonIntegerDigits)
        {
            throw new InvalidDataException("JSON integer exceeds Python's 4,300-digit limit.");
        }
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

        var seenProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectTiming in projects.EnumerateArray())
        {
            if (projectTiming.ValueKind != JsonValueKind.Object ||
                !projectTiming.TryGetProperty("project", out var projectValue) ||
                projectValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("project must be a safe relative .csproj path.");
            }

            var project = ReadProjectString(projectValue);
            if (!IsSafeProjectPath(project))
            {
                throw new InvalidDataException("project must be a safe relative .csproj path.");
            }

            if (!seenProjects.Add(CaseFold(project)))
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
        if (project.Length == 0 || CountCodePoints(project) > MaxProjectPathCodePoints ||
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

    private static int CountCodePoints(string value)
    {
        var count = 0;
        var index = 0;
        while (index < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out _, out var consumed);
            index += status == OperationStatus.Done ? consumed : 1;
            count++;
        }

        return count;
    }

    /// <summary>Applies the Ubuntu Python baseline's Unicode folding for project and repository comparisons.</summary>
    internal static string CaseFold(string value)
    {
        var folded = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                folded.Append(value[index]);
                index++;
                continue;
            }

            if (FullCaseFolds.TryGetValue(rune.Value, out var mapping))
            {
                folded.Append(mapping);
            }
            else
            {
                folded.Append(rune);
            }

            index += consumed;
        }

        return folded.ToString();
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

    private static Dictionary<int, string> CreateFullCaseFolds()
    {
        var mappings = new Dictionary<int, string>(1530);
        using var reader = new StringReader(CoverageHistoryCaseFoldData.Mappings);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf(';');
            var source = int.Parse(
                line.AsSpan(0, separator),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            var folded = new StringBuilder();
            foreach (var codePointText in line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var codePoint = int.Parse(codePointText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                folded.Append(new Rune(codePoint));
            }

            mappings.Add(source, folded.ToString());
        }

        return mappings;
    }
}
