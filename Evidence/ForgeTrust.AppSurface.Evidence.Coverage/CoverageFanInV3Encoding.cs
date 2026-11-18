using System.Globalization;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>V3 strict wire substrate: ordinal properties, normalized decimal numbers, explicit nulls and UTF8 without BOM.</summary>
/// <remarks>Writers and independent readers may share this encoding, never a coverage classification or hunk parser.</remarks>
internal static class CoverageFanInV3Encoding
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static JsonElement Element(object value)
    {
        using var stream = new WireOutput();
        JsonSerializer.Serialize(stream, value, Json);
        using var document = JsonDocument.Parse(stream.ToArray(), new() { MaxDepth = 64 });
        return document.RootElement.Clone();
    }

    internal static byte[] Canonical(object value)
    {
        var element = value is JsonElement json ? json : Element(value);
        using var stream = new WireOutput();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return stream.ToArray();
    }

    private sealed class WireOutput : MemoryStream
    {
        private void Admit(int count)
        {
            if (count > CoverageFanInDiffContext.StreamLimit - Position)
                throw new CoverageFanInEvidenceException("limit-exceeded", "json", "The v3 artifact exceeds the existing 1 MiB wire limit; preserve fixed budgets and ask the maintainer to resolve supported input scope.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Admit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Admit(1); base.WriteByte(value); }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetDecimal().ToString("0.############################", CultureInfo.InvariantCulture)); break;
            default: element.WriteTo(writer); break;
        }
    }
}
