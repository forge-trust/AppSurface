using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Distinguishes present aggregates from scalars and null members skipped during layering.</summary>
internal enum ConfigFileValueShape
{
    Scalar,
    Object,
    Array,
    Null
}

/// <summary>One validated occurrence with its copy-owned document bytes and original token coordinates.</summary>
/// <param name="Key">The logical path constructed directly from JSON property and index segments.</param>
/// <param name="SourceSpelling">The original case-preserving logical spelling.</param>
/// <param name="Shape">The token shape, including empty aggregates and nulls.</param>
/// <param name="Bytes">The captured UTF-8 document without a BOM; never the caller's mutable buffer.</param>
/// <param name="ValueStart">The zero-based byte offset of the first value token.</param>
/// <param name="ValueLength">The byte length through the end of the value token or aggregate.</param>
/// <param name="Location">One-based property-name or array-item coordinates within the captured document.</param>
/// <param name="ScalarValue">The scalar value, or null for aggregates and JSON null.</param>
internal sealed record ConfigFileProjectedEntry(
    AppSurfaceConfigKey Key,
    string SourceSpelling,
    ConfigFileValueShape Shape,
    byte[] Bytes,
    int ValueStart,
    int ValueLength,
    ConfigAuditSourceLocation Location,
    object? ScalarValue);

/// <summary>Projects one JSON document directly from its UTF-8 token stream.</summary>
internal sealed class ConfigFileTokenProjection
{
    private ConfigFileTokenProjection(IReadOnlyDictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> entries,
        IReadOnlyList<ConfigFileProjectedEntry> occurrences, IReadOnlySet<AppSurfaceConfigKey> terminalKeys, IReadOnlySet<AppSurfaceConfigKey> invalidKeys,
        ConfigFileSourceLocationMap locations, JsonObject materializedRoot)
    {
        Entries = entries;
        Occurrences = occurrences;
        TerminalKeys = terminalKeys;
        InvalidKeys = invalidKeys;
        Locations = locations;
        MaterializedRoot = materializedRoot;
    }

    /// <summary>Gets the first occurrence per identity; callers must check terminal domains before selecting values.</summary>
    internal IReadOnlyDictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> Entries { get; }
    /// <summary>Gets every valid occurrence, including duplicates needed for cross-layer collision validation.</summary>
    internal IReadOnlyList<ConfigFileProjectedEntry> Occurrences { get; }
    /// <summary>Gets ambiguous or unrepresentable keys and their affected aggregate ancestors.</summary>
    internal IReadOnlySet<AppSurfaceConfigKey> TerminalKeys { get; }
    /// <summary>Gets terminal domains caused specifically by an invalid source segment.</summary>
    internal IReadOnlySet<AppSurfaceConfigKey> InvalidKeys { get; }
    /// <summary>Gets exact token coordinates, with terminal locations suppressed.</summary>
    internal ConfigFileSourceLocationMap Locations { get; }
    /// <summary>Gets the tree materialized from these same tokens; terminal paths are never safe lookup results.</summary>
    internal JsonObject MaterializedRoot { get; }

    /// <summary>Captures and projects one document without discarding duplicate evidence in a DOM parser.</summary>
    /// <param name="input">UTF-8 JSON with an optional BOM. Empty input represents an empty file layer.</param>
    /// <returns>The complete projection, including invalid subtrees that poison valid ancestors.</returns>
    /// <exception cref="JsonException">The document is malformed or its root is not an object.</exception>
    internal static ConfigFileTokenProjection Parse(ReadOnlySpan<byte> input)
    {
        var bytes = input.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? input[3..].ToArray() : input.ToArray();
        var entries = new Dictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry>();
        var terminals = new HashSet<AppSurfaceConfigKey>();
        var invalidKeys = new HashSet<AppSurfaceConfigKey>();
        var locations = new Dictionary<string, ConfigAuditSourceLocation?>(StringComparer.OrdinalIgnoreCase);
        var root = new JsonObject();
        var occurrences = new List<ConfigFileProjectedEntry>();
        if (bytes.Length == 0) return new(entries, occurrences, terminals, invalidKeys, new(locations), root);

        var starts = BuildLineStarts(bytes);
        var reader = new Utf8JsonReader(bytes, true, default);
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The configuration document root must be an object.");
        var invalidRootProperty = ReadObject(ref reader, [], bytes, starts, entries, occurrences, terminals, invalidKeys, locations, root);
        if (invalidRootProperty && entries.Count == 0)
            throw new JsonException("The configuration document has no representable root properties.");
        // The final-block reader validates termination and rejects trailing data itself.
        reader.Read();
        foreach (var terminal in terminals) locations[terminal.Value] = null;
        return new(entries, occurrences, terminals, invalidKeys, new(locations), root);
    }

    private static bool ReadObject(ref Utf8JsonReader reader, IReadOnlyList<string> parent, byte[] bytes, int[] starts,
        Dictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> entries, List<ConfigFileProjectedEntry> occurrences,
        HashSet<AppSurfaceConfigKey> terminals, HashSet<AppSurfaceConfigKey> invalidKeys,
        Dictionary<string, ConfigAuditSourceLocation?> locations, JsonObject target)
    {
        var invalidRootProperty = false;
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            // Utf8JsonReader guarantees a property token here and a value token after Read.
            var propertyStart = checked((int)reader.TokenStartIndex);
            var spelling = reader.GetString()!;
            var segments = parent.Append(spelling).ToArray();
            if (parent.Count == 0 && !TryKey(segments, out _))
                invalidRootProperty = true;
            reader.Read();
            var start = checked((int)reader.TokenStartIndex);
            var child = ReadValue(ref reader, segments, bytes, starts, entries, occurrences, terminals, invalidKeys, locations);
            if (Record(segments, start, checked((int)reader.BytesConsumed), propertyStart, child.Shape, child.Scalar))
                target[spelling] = child.Node;
            reader.Read();
        }

        return invalidRootProperty;

        bool Record(string[] path, int start, int end, int locator, ConfigFileValueShape shape, object? scalar) =>
            RecordEntry(path, bytes, starts, start, end, locator, shape, scalar, entries, occurrences, terminals, invalidKeys, locations);
    }

    private static (ConfigFileValueShape Shape, object? Scalar, JsonNode? Node) ReadValue(ref Utf8JsonReader reader,
        IReadOnlyList<string> path, byte[] bytes, int[] starts, Dictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> entries,
        List<ConfigFileProjectedEntry> occurrences,
        HashSet<AppSurfaceConfigKey> terminals, HashSet<AppSurfaceConfigKey> invalidKeys, Dictionary<string, ConfigAuditSourceLocation?> locations)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new JsonObject();
                ReadObject(ref reader, path, bytes, starts, entries, occurrences, terminals, invalidKeys, locations, obj);
                return (ConfigFileValueShape.Object, null, obj);
            case JsonTokenType.StartArray:
                var array = new JsonArray();
                ReadArray(ref reader, path, bytes, starts, entries, occurrences, terminals, invalidKeys, locations, array);
                return (ConfigFileValueShape.Array, null, array);
            case JsonTokenType.Null:
                return (ConfigFileValueShape.Null, null, null);
            case JsonTokenType.String:
                var text = reader.GetString();
                return (ConfigFileValueShape.Scalar, text, JsonValue.Create(text));
            case JsonTokenType.True:
                return (ConfigFileValueShape.Scalar, true, JsonValue.Create(true));
            case JsonTokenType.False:
                return (ConfigFileValueShape.Scalar, false, JsonValue.Create(false));
            default:
                // All other value tokens are numbers; the reader has already validated JSON grammar.
                object number = reader.TryGetInt64(out var integer) ? (object)integer
                    : reader.TryGetDecimal(out var fraction) ? fraction : reader.GetDouble();
                return (ConfigFileValueShape.Scalar, number, JsonValue.Create(number));
        }
    }

    private static void ReadArray(ref Utf8JsonReader reader, IReadOnlyList<string> parent, byte[] bytes, int[] starts,
        Dictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> entries, List<ConfigFileProjectedEntry> occurrences,
        HashSet<AppSurfaceConfigKey> terminals, HashSet<AppSurfaceConfigKey> invalidKeys,
        Dictionary<string, ConfigAuditSourceLocation?> locations, JsonArray target)
    {
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            var path = parent.Append(target.Count.ToString(CultureInfo.InvariantCulture)).ToArray();
            var start = checked((int)reader.TokenStartIndex);
            var child = ReadValue(ref reader, path, bytes, starts, entries, occurrences, terminals, invalidKeys, locations);
            RecordEntry(path, bytes, starts, start, checked((int)reader.BytesConsumed), start, child.Shape, child.Scalar,
                entries, occurrences, terminals, invalidKeys, locations);
            target.Add(child.Node);
            reader.Read();
        }
    }

    /// <summary>Retains duplicate occurrences before lookup can erase them; only unique valid nodes enter the bound tree.</summary>
    private static bool RecordEntry(IReadOnlyList<string> path, byte[] bytes, int[] starts, int start, int end,
        int locator, ConfigFileValueShape shape, object? scalar,
        Dictionary<AppSurfaceConfigKey, ConfigFileProjectedEntry> entries, List<ConfigFileProjectedEntry> occurrences,
        HashSet<AppSurfaceConfigKey> terminals, HashSet<AppSurfaceConfigKey> invalidKeys,
        Dictionary<string, ConfigAuditSourceLocation?> locations)
    {
        if (!TryKey(path, out var key))
        {
            MarkInvalidDomain(path, terminals, invalidKeys, locations);
            return false;
        }
        var entry = new ConfigFileProjectedEntry(key!, key!.Value, shape, bytes, start, end - start,
            CreateLocation(locator, starts), scalar);
        occurrences.Add(entry);
        if (!entries.TryAdd(key, entry))
        {
            foreach (var ancestor in key.AncestorsAndSelf()) terminals.Add(ancestor);
            return false;
        }
        locations[key.Value] = entry.Location;
        return true;
    }

    private static bool TryKey(IReadOnlyList<string> segments, out AppSurfaceConfigKey? key)
    {
        try
        {
            key = AppSurfaceConfigKey.FromSegments(segments.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            key = null;
            return false;
        }
    }

    private static void MarkInvalidDomain(IReadOnlyList<string> segments, HashSet<AppSurfaceConfigKey> terminals, HashSet<AppSurfaceConfigKey> invalidKeys,
        Dictionary<string, ConfigAuditSourceLocation?> locations)
    {
        for (var count = segments.Count; count > 0; count--)
        {
            if (TryKey(segments.Take(count).ToArray(), out var key))
            {
                foreach (var ancestor in key!.AncestorsAndSelf())
                {
                    terminals.Add(ancestor);
                    invalidKeys.Add(ancestor);
                    locations[ancestor.Value] = null;
                }
            }
        }
    }

    private static int[] BuildLineStarts(ReadOnlySpan<byte> bytes)
    {
        var result = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') i++;
                result.Add(i + 1);
            }
            else if (bytes[i] == (byte)'\n')
            {
                result.Add(i + 1);
            }
        }
        return [.. result];
    }

    private static ConfigAuditSourceLocation CreateLocation(int offset, int[] starts)
    {
        var line = Array.BinarySearch(starts, offset);
        if (line < 0) line = ~line - 1;
        return new(line + 1, offset - starts[line] + 1);
    }
}

/// <summary>Shared typed ancestry for poisoning a file key and each containing aggregate.</summary>
internal static class ConfigFileTokenProjectionExtensions
{
    /// <summary>Enumerates ancestors from the root segment through the complete key.</summary>
    /// <param name="key">The validated source identity.</param>
    /// <returns>Case-preserving typed ancestors, including the key itself.</returns>
    internal static IEnumerable<AppSurfaceConfigKey> AncestorsAndSelf(this AppSurfaceConfigKey key)
    {
        for (var count = 1; count <= key.Segments.Length; count++)
            yield return AppSurfaceConfigKey.FromSegments(key.Segments.Take(count).ToArray());
    }
}
