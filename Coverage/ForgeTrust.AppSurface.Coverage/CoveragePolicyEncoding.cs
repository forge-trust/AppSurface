using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Coverage;

/// <summary>Strict wire decoding and coverage-policy-c14n-1 typed snapshots; no legacy canonicalizer dependency.</summary>
/// <remarks>
/// Canonical bytes are UTF-8 without a BOM or whitespace. Object properties use ordinal order; project and supplement
/// inventories are ordinally sorted sets, and strings use the frozen default JSON encoder without NFC folding. Integer
/// values must be lossless signed 32-bit or 64-bit values. Decimal policy values are lossless decimals; gate values are
/// limited to 0 through 100 with at most six fractional digits. Byte limits are positive, XML/JSON limits are at most
/// <see cref="int.MaxValue"/>, and the total-byte limit is at most 1 TiB. Depth is 1–256, bundles 1–1024, operation
/// seconds 1–86400, identity seconds 1–300, and other count/token limits are positive 32-bit integers. Relative project
/// paths use forward slashes and reject traversal, platform aliases, controls, format characters, and invalid scalars.
/// The policy digest is SHA-256 over the encoding-version line, then these exact canonical bytes.
/// </remarks>
internal static partial class CoveragePolicyEncoding
{
    internal const string Version = "coverage-policy-c14n-1";
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64,
            Encoder = JavaScriptEncoder.Default,
            Converters =
            {
                new LosslessInt32Converter(),
                new LosslessInt64Converter(),
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
            }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    internal static CoverageFanInPolicy Freeze(CoverageFanInPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Source is null || policy.Run is null || policy.Gate is null
            || policy.TestProjects is null || policy.Supplements is null)
            throw Invalid("policy");
        if (policy.TestProjects.Count is < 1 or > 10000 || policy.Supplements.Count > 1023)
            throw Invalid("inventory");
        var source = policy.Source;
        if (!HexIdentity().IsMatch(source.Commit ?? "") || !HexIdentity().IsMatch(source.Tree ?? "")
            || !SdkIdentity().IsMatch(source.Sdk ?? "")) throw Invalid("source");
        Text(source.Configuration, "configuration");
        Text(policy.Run.ProducerSystem, "producerSystem"); Text(policy.Run.RunScope, "runScope");
        Text(policy.Run.RunId, "runId");
        if (policy.Run.Attempt < 1) throw Invalid("attempt");
        var projects = policy.TestProjects.Select(Relative).ToArray();
        EnsureUniqueProjectAliases(projects, "testProjects");
        Array.Sort(projects, StringComparer.Ordinal);
        var supplements = policy.Supplements.Select(x =>
        {
            if (x is null || StringComparer.OrdinalIgnoreCase.Equals(x.Id, "primary")) throw Invalid("supplements");
            Token(x.Id, "producerId"); Token(x.Platform, "platform"); Text(x.Filter, "filter", 4096);
            if (x.TestProjects is { Count: > 10000 }) throw Invalid("supplementTestProjects");
            var inventory = x.TestProjects?.Select(Relative).ToArray();
            if (inventory is { Length: 0 }) throw Invalid("supplementTestProjects");
            if (inventory is not null)
            {
                EnsureUniqueProjectAliases(inventory, "supplementTestProjects");
                Array.Sort(inventory, StringComparer.Ordinal);
            }
            return x with { TestProjects = inventory is null ? null : Array.AsReadOnly(inventory) };
        }).ToArray();
        var supplementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplement in supplements)
            if (!supplementIds.Add(supplement.Id)) throw Invalid("supplements");
        Array.Sort(supplements, static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var gate = policy.Gate;
        if (!Enum.IsDefined(gate.Mode) || new[] { gate.MinLine, gate.MinBranch, gate.MinPatchLine,
                gate.MinPatchBranch, gate.Tolerance }.Any(x => x < 0 || x > 100 || decimal.Round(x, 6) != x)
            || gate.PatchLineMode is not ("codecov" or "measurable")) throw Invalid("gate");
        if (gate.Mode == CoverageGateMode.AggregateAndPatch)
        {
            Text(gate.DiffBase, "diffBase");
            if (gate.DiffBase!.StartsWith('-')) throw Invalid("diffBase");
        }
        else if (gate.DiffBase is not null) throw Invalid("diffBase");
        var instrumentation = policy.Instrumentation ?? new();
        if (instrumentation.Driver != "collector" || instrumentation.Format != "cobertura"
            || instrumentation.Normalization != "appsurface-coverage-merge") throw Invalid("instrumentation");
        Text(instrumentation.Exclude, "exclude", 4096);
        if (instrumentation.Include is not null) Text(instrumentation.Include, "include", 4096);
        var limits = policy.Limits ?? new();
        if (limits.XmlBytes < 1 || limits.JsonBytes < 1 || limits.TotalBytes < 1
            || limits.XmlBytes > int.MaxValue || limits.JsonBytes > int.MaxValue
            || limits.TotalBytes > 1099511627776 || limits.Depth is < 1 or > 256
            || limits.LineRecordsPerReport < 1 || limits.InputBundles is < 1 or > 1024
            || limits.XmlElementsPerReport < 1 || limits.AttributesPerElement < 1
            || limits.TokenCharacters < 1 || limits.BranchRecordsPerReport < 1 || limits.TotalRecords < 1
            || limits.OperationSeconds is < 1 or > 86400 || limits.IdentitySeconds is < 1 or > 300
            || supplements.Length + 1 > limits.InputBundles) throw Invalid("limits");
        return policy with { TestProjects = Array.AsReadOnly(projects), Supplements = Array.AsReadOnly(supplements),
            Instrumentation = instrumentation with { }, Limits = limits with { },
            Source = source with { }, Run = policy.Run with { }, Gate = gate with { } };
    }

    internal static byte[] CanonicalBytes(CoverageFanInPolicy policy)
    {
        var frozen = Freeze(policy);
        var element = JsonSerializer.SerializeToElement(frozen, Json);
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory, new() { Encoder = JavaScriptEncoder.Default })) Write(writer, element);
        return memory.ToArray();
    }

    internal static string Digest(CoverageFanInPolicy policy)
    {
        var prefix = Encoding.UTF8.GetBytes(Version + "\n");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix); hash.AppendData(CanonicalBytes(policy));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(p.Name); Write(writer, p.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var x in element.EnumerateArray()) Write(writer, x);
                writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetDecimal().ToString("0.############################", CultureInfo.InvariantCulture)); break;
            default: element.WriteTo(writer); break;
        }
    }

    internal static T Decode<T>(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 64 });
        CheckSyntax(doc.RootElement);
        var decoded = doc.RootElement.Deserialize<T>(Json) ?? throw Invalid("request");
        return decoded is CoverageFanInPolicy policy ? (T)(object)Freeze(policy) : decoded;
    }

    internal static void CheckSyntax(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw Invalid("duplicateField");
                Unicode(p.Name); CheckSyntax(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckSyntax(item);
        else if (value.ValueKind == JsonValueKind.String)
        {
            ValidateEscapedSurrogates(value.GetRawText());
            Unicode(value.GetString()!);
        }
        else if (value.ValueKind == JsonValueKind.Number) ValidateLosslessNumber(value);
    }

    internal static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\')
            || path.StartsWith('/') || path.Split('/').Any(x => x is "" or "." or ".."
                || x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0
                || IsWindowsDeviceName(x))
            || path.Any(char.IsControl)) throw Invalid("relativePath");
        Unicode(path); return path;
    }
    private static void Token(string? value, string field)
    {
        Text(value, field);
        if (value!.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw Invalid(field);
    }
    private static void Text(string? value, string field, int maximum = 256)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw Invalid(field);
        Unicode(value);
    }
    private static void Unicode(string value)
    {
        for (var i = 0; i < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, i, out var rune)) throw Invalid("unicode");
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || rune.Value is >= 0xFDD0 and <= 0xFDEF || (rune.Value & 0xFFFE) == 0xFFFE)
                throw Invalid("unicode");
            i += rune.Utf16SequenceLength;
        }
    }

    private static void EnsureUniqueProjectAliases(IEnumerable<string> paths, string field)
    {
        var normalizedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!normalizedAliases.Add(path.Normalize(NormalizationForm.FormC)))
                throw Invalid(field);
        }
    }

    private static bool IsWindowsDeviceName(string component)
    {
        var dot = component.IndexOf('.');
        var stem = dot < 0 ? component : component[..dot];
        if (StringComparer.OrdinalIgnoreCase.Equals(stem, "CONIN$")
            || StringComparer.OrdinalIgnoreCase.Equals(stem, "CONOUT$")) return true;
        if (stem.Length == 4 && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))) return true;
        return StringComparer.OrdinalIgnoreCase.Equals(stem, "CON")
            || StringComparer.OrdinalIgnoreCase.Equals(stem, "PRN")
            || StringComparer.OrdinalIgnoreCase.Equals(stem, "AUX")
            || StringComparer.OrdinalIgnoreCase.Equals(stem, "NUL");
    }

    private static void ValidateLosslessNumber(JsonElement value)
    {
        if (!value.TryGetDecimal(out var decoded)) throw Invalid("number");
        var raw = value.GetRawText();
        if (raw.Length > 256) throw Invalid("number");
        var rawValue = ParseDecimal(raw);
        var decimalValue = DecimalParts(decoded);
        if (rawValue != decimalValue) throw Invalid("number");
    }

    private static void ValidateEscapedSurrogates(string raw)
    {
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\') continue;
            if (raw[i + 1] != 'u')
            {
                i++;
                continue;
            }

            if (!TryCodeUnit(raw, i + 2, out var codeUnit)) throw Invalid("unicode");
            if (char.IsLowSurrogate((char)codeUnit)) throw Invalid("unicode");
            if (char.IsHighSurrogate((char)codeUnit))
            {
                if (i + 11 >= raw.Length - 1 || raw[i + 6] != '\\' || raw[i + 7] != 'u'
                    || !TryCodeUnit(raw, i + 8, out var low) || !char.IsLowSurrogate((char)low))
                    throw Invalid("unicode");
                i += 11;
            }
            else
            {
                i += 5;
            }
        }
    }

    private static bool TryCodeUnit(string raw, int start, out int codeUnit)
    {
        if (start + 4 > raw.Length)
        {
            codeUnit = 0;
            return false;
        }
        return int.TryParse(raw.AsSpan(start, 4), NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out codeUnit);
    }

    private sealed class LosslessInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var value)
                || value != decimal.Truncate(value) || value < int.MinValue || value > int.MaxValue)
                throw new JsonException("Expected a lossless Int32 value.");
            return decimal.ToInt32(value);
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    private sealed class LosslessInt64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var value)
                || value != decimal.Truncate(value) || value < long.MinValue || value > long.MaxValue)
                throw new JsonException("Expected a lossless Int64 value.");
            return decimal.ToInt64(value);
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    private static (BigInteger Coefficient, int Scale) ParseDecimal(string raw)
    {
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var exponent = 0;
        if (exponentIndex >= 0 && (!int.TryParse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent is < -1000 or > 1000))
            throw Invalid("number");

        var negative = mantissa[0] == '-';
        var unsigned = negative ? mantissa[1..] : mantissa;
        var decimalIndex = unsigned.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : unsigned.Length - decimalIndex - 1;
        var digits = decimalIndex < 0 ? unsigned : string.Concat(unsigned.AsSpan(0, decimalIndex), unsigned.AsSpan(decimalIndex + 1));
        var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative) coefficient = -coefficient;
        var scale = fractionalDigits - exponent;
        if (scale < 0)
        {
            coefficient *= BigInteger.Pow(10, -scale);
            scale = 0;
        }
        return NormalizeDecimal(coefficient, scale);
    }

    private static (BigInteger Coefficient, int Scale) DecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0]
            | (BigInteger)(uint)bits[1] << 32
            | (BigInteger)(uint)bits[2] << 64;
        if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
        return NormalizeDecimal(coefficient, (bits[3] >> 16) & 0x7F);
    }

    private static (BigInteger Coefficient, int Scale) NormalizeDecimal(BigInteger coefficient, int scale)
    {
        if (coefficient.IsZero) return (BigInteger.Zero, 0);
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
        return (coefficient, scale);
    }
    internal static ArgumentException Invalid(string field) => new("ASCOV301 invalid " + field + "; correct the explicit request before retrying.", field);
    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex HexIdentity();
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+([+-][A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkIdentity();
}
