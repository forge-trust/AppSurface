using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>
/// Serializes contract objects deterministically for evidence identity and verification, and strictly ingests bounded JSON.
/// </summary>
public static class EvidenceCanonicalJson
{
    /// <summary>
    /// Gets the protected maximum number of UTF-8 bytes accepted by Evidence JSON deserialization.
    /// Callers may configure a lower per-input limit, but cannot raise this ceiling.
    /// </summary>
    public const int MaximumInputBytes = 20 * 1024 * 1024;

    /// <summary>The maximum JSON nesting depth for canonical serialization and bounded reads.</summary>
    internal const int MaximumDepth = 64;

    /// <summary>
    /// Serializes a value as canonical UTF-8 JSON with ordinal object-property order.
    /// </summary>
    /// <typeparam name="TValue">Value type to serialize.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    public static byte[] Serialize<TValue>(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElement(document.RootElement, writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Deserializes a bounded JSON document using the contract serializer options.
    /// Object properties must be unique under ordinal, case-insensitive comparison at every depth. Enum values must
    /// be known strings. Required constructor members must be present and non-null, and contract collections must
    /// not contain null items. Optional nullable members and constructor defaults remain supported.
    /// Unknown properties are ignored for additive compatibility, and plan and manifest values
    /// must declare the currently supported contract version (<c>1.0</c>).
    /// These checks also apply to typed contract values nested in caller-owned collections or wrapper objects.
    /// </summary>
    /// <typeparam name="TValue">Value type to deserialize.</typeparam>
    /// <param name="utf8Json">JSON bytes already held in memory.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="InvalidDataException">The input exceeds <see cref="MaximumInputBytes"/>.</exception>
    /// <exception cref="JsonException">The input is malformed, ambiguous, contains an invalid enum, or uses an unsupported plan or manifest version.</exception>
    public static TValue Deserialize<TValue>(ReadOnlySpan<byte> utf8Json)
    {
        return Deserialize<TValue>(utf8Json, MaximumInputBytes);
    }

    /// <summary>
    /// Deserializes a bounded JSON document using a caller-selected limit that cannot exceed
    /// <see cref="MaximumInputBytes"/>. This overload is useful when a protected input source has a tighter budget.
    /// </summary>
    /// <typeparam name="TValue">Value type to deserialize.</typeparam>
    /// <param name="utf8Json">JSON bytes already held in memory.</param>
    /// <param name="maximumBytes">Optional lower per-input limit, from zero through <see cref="MaximumInputBytes"/>.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is outside the permitted range.</exception>
    /// <exception cref="InvalidDataException">The input exceeds <paramref name="maximumBytes"/>.</exception>
    /// <exception cref="JsonException">The input is malformed, ambiguous, contains an invalid enum, or uses an unsupported plan or manifest version.</exception>
    public static TValue Deserialize<TValue>(ReadOnlySpan<byte> utf8Json, int maximumBytes) =>
        EvidenceJsonReader.Deserialize<TValue>(utf8Json, maximumBytes);

    /// <summary>
    /// Reads and deserializes a JSON document from a stream while counting bytes as they arrive. The stream is not
    /// owned or disposed by this method. Reading stops with a safe error after observing the first byte above the
    /// configured limit; seekability and advertised stream length are not used as bounds.
    /// </summary>
    /// <typeparam name="TValue">Value type to deserialize.</typeparam>
    /// <param name="utf8Json">Readable source stream.</param>
    /// <param name="maximumBytes">Per-input limit, from zero through <see cref="MaximumInputBytes"/>.</param>
    /// <param name="cancellationToken">Token used to cancel stream reading.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is outside the permitted range.</exception>
    /// <exception cref="InvalidDataException">The input exceeds <paramref name="maximumBytes"/>, or reading throws <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>, <see cref="NotSupportedException"/> or <see cref="ObjectDisposedException"/>.</exception>
    /// <exception cref="JsonException">The input is malformed, ambiguous, contains an invalid enum, or uses an unsupported plan or manifest version.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public static ValueTask<TValue> DeserializeAsync<TValue>(
        Stream utf8Json,
        int maximumBytes = MaximumInputBytes,
        CancellationToken cancellationToken = default) =>
        EvidenceJsonReader.DeserializeAsync<TValue>(utf8Json, maximumBytes, cancellationToken);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        MaxDepth = MaximumDepth,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON token {element.ValueKind}.");
        }
    }
}
