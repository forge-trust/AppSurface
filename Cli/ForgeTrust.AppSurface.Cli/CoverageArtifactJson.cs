using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Reads bounded private metadata, rejecting ambiguous duplicate keys before deserialization.</summary>
internal static class CoverageArtifactJson
{
    /// <summary>Reads at most the metadata limit plus one byte, even if an opened file grows.</summary>
    /// <param name="stream">Caller-owned stream positioned at the document start.</param>
    /// <param name="maximumBytes">Strict encoded-byte admission limit.</param>
    /// <param name="options">Schema-specific serializer options, including the nesting bound.</param>
    /// <typeparam name="T">Expected metadata record type.</typeparam>
    /// <returns>The unambiguous schema record; callers validate its semantic facts.</returns>
    internal static T Read<T>(Stream stream, long maximumBytes, JsonSerializerOptions options) where T : class
    {
        var buffer = new byte[checked((int)maximumBytes + 1)];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, Math.Min(81920, buffer.Length - count));
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException("Artifact metadata exceeds its encoded-byte limit.");
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = options.MaxDepth });
            ValidateKeys(document.RootElement);
            return document.RootElement.Deserialize<T>(options) ?? throw new InvalidDataException("Artifact metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Artifact metadata is invalid or uses an unsupported shape.", exception);
        }
    }

    private static void ValidateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Artifact metadata contains duplicate keys.");
                ValidateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) ValidateKeys(child);
    }
}
