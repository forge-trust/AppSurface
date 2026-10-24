using System.Collections.Immutable;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

internal sealed record OciExpectedFile(string Path, string Sha256, string Type = "file", int? Mode = null, long? Uid = null, long? Gid = null);
internal sealed record OciReadSelection(IReadOnlySet<string> Paths, string AbsentOutputDirectory);
internal sealed record OciExpectedImage(string? IndexDigest, string ManifestDigest, string Platform, string ConfigDigest, IReadOnlyList<string> DiffIds, IReadOnlyDictionary<string, OciExpectedFile>? SelectedFiles = null, IReadOnlySet<string>? PayloadPaths = null, string? ConfigUser = null, IReadOnlyList<string>? ConfigEntrypoint = null, IReadOnlyDictionary<string, string>? ConfigEnvironment = null);
internal sealed record OciReaderLimits(long MaxArchiveBytes = 1L * 1024 * 1024 * 1024, long MaxCompressedBlobBytes = 1L * 1024 * 1024 * 1024, long MaxExpandedLayerBytes = 2L * 1024 * 1024 * 1024, long MaxExpandedTotalBytes = 4L * 1024 * 1024 * 1024, int MaxJsonBytes = 1 * 1024 * 1024, int MaxLayers = 64, int MaxLayerMembers = 250_000, int MaxTotalMembers = 1_000_000, int MaxPaths = 1_000_000, int MaxPathLength = 4096, int MaxPayloadBytes = 1 * 1024 * 1024, int MaxPayloadAggregateBytes = 4 * 1024 * 1024, long MaxSelectedOutputBytes = 1L * 1024 * 1024 * 1024, long MaxLayerEntryBytes = 128L * 1024 * 1024);
internal sealed record ObservedFile(string Path, string Type, string Sha256, long Length, int Mode, long Uid, long Gid, string? LinkTarget, int Layer, byte[]? TargetedPayload = null);
internal sealed record ObservedOciImage(string IndexDigest, string ManifestDigest, string Platform, string ConfigDigest, string ConfigUser, ImmutableArray<string> ConfigEntrypoint, ImmutableDictionary<string, string> ConfigEnvironment, ImmutableArray<string> ConfigPorts, ImmutableArray<string> DiffIds, ImmutableArray<ObservedFile> Entries, ImmutableArray<string> InstalledDpkgStatus, OciReaderLimits Limits, long CompressedBytes, long ExpandedBytes);
/// <summary>Private bounded reader for exact, offline inspection of Docker save OCI archives.</summary>
internal sealed class OciReader
{
    internal static readonly OciReaderLimits DefaultLimits = new();
    internal static readonly OciReaderLimits SourceCaptureLimits = new(MaxLayerEntryBytes: 256L * 1024 * 1024);
    private readonly OciReaderLimits limits;
    internal OciReader(OciReaderLimits? limits = null)
    {
        this.limits = limits ?? DefaultLimits;
        ValidateLimits(this.limits);
    }

    private static void ValidateLimits(OciReaderLimits value)
    {
        if (value.MaxArchiveBytes <= 0 || value.MaxCompressedBlobBytes <= 0 || value.MaxExpandedLayerBytes <= 0 || value.MaxExpandedTotalBytes <= 0 || value.MaxJsonBytes <= 0 || value.MaxLayers <= 0 || value.MaxLayerMembers <= 0 || value.MaxTotalMembers <= 0 || value.MaxPaths <= 0 || value.MaxPathLength <= 0 || value.MaxPayloadBytes <= 0 || value.MaxPayloadAggregateBytes <= 0 || value.MaxSelectedOutputBytes <= 0 || value.MaxLayerEntryBytes <= 0 || value.MaxLayerEntryBytes > value.MaxExpandedLayerBytes || value.MaxPayloadAggregateBytes < value.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "OCI limits must be positive and internally bounded.");
        }
    }

    /// <summary>Reads and verifies one exact OCI image without extraction or caller supplied security evidence.</summary>
    internal ObservedOciImage Read(string ownedArchive, OciExpectedImage expected, CancellationToken cancellationToken = default) => Read(ownedArchive, expected, cancellationToken, null);
    internal ObservedOciImage Read(string ownedArchive, OciExpectedImage expected, OciReadSelection selection, CancellationToken cancellationToken = default) => Read(ownedArchive, expected, cancellationToken, selection);
    private ObservedOciImage Read(string ownedArchive, OciExpectedImage expected, CancellationToken cancellationToken, OciReadSelection? selection)
    {
        if (string.IsNullOrWhiteSpace(ownedArchive) || expected is null)
            throw new ArgumentNullException(nameof(ownedArchive));
        var deadline = new Deadline(cancellationToken, limits.MaxPayloadAggregateBytes);
        FileInfo archive = new(ownedArchive);
        if (!archive.Exists || archive.Length > limits.MaxArchiveBytes)
            throw new InvalidDataException("Archive exceeds the bounded profile.");
        SelectionState? selectedState = selection is null ? null : SelectionState.Create(selection, limits);
        string scratch = Path.Combine(Path.GetTempPath(), "forge-oci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            return ReadArchive(ownedArchive, expected, scratch, deadline, selectedState);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, true);
            }
            catch
            {
            }

            selectedState?.Cleanup();
        }
    }

    private ObservedOciImage ReadArchive(string path, OciExpectedImage expected, string scratch, Deadline d, SelectionState? selectedState)
    {
        var blobs = new Dictionary<string, Blob>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long compressed = 0;
        using (var file = File.OpenRead(path))
        using (var bounded = new TarBudgetReadStream(file, limits.MaxArchiveBytes, limits.MaxCompressedBlobBytes, limits.MaxPaths, limits.MaxJsonBytes))
        using (var tar = new TarReader(bounded))
        {
            TarEntry? e;
            while ((e = tar.GetNextEntry()) is not null)
            {
                d.Check();
                string name = SafePath(e.Name.TrimEnd('/'));
                d.AccountMetadata(name);
                if (!names.Add(name))
                    throw new InvalidDataException("Duplicate archive path.");
                if (e.EntryType is TarEntryType.Directory)
                    continue;
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Unsupported outer tar member.");
                if (name.StartsWith("blobs/sha256/", StringComparison.Ordinal))
                {
                    string hex = name[13..];
                    if (hex.Length != 64 || hex.Any(c => !Uri.IsHexDigit(c)) || hex != hex.ToLowerInvariant())
                        throw new InvalidDataException("Invalid blob name.");
                    if (e.Length > limits.MaxCompressedBlobBytes || checked(compressed + e.Length) > limits.MaxArchiveBytes)
                        throw new InvalidDataException("Compressed blob budget exceeded.");
                    string target = Path.Combine(scratch, hex);
                    Spool(e.DataStream!, target, e.Length, "sha256:" + hex, d);
                    blobs["sha256:" + hex] = new Blob(target, e.Length);
                    compressed += e.Length;
                }
                else if (name is "oci-layout" or "index.json" or "manifest.json")
                {
                    if (e.Length > limits.MaxJsonBytes)
                        throw new InvalidDataException("Metadata JSON exceeds the bounded profile.");
                    metadata[name] = ReadBounded(e.DataStream!, checked((int)e.Length), d);
                }
                else
                    throw new InvalidDataException("Unsupported outer archive path.");
            }
        }

        using JsonDocument layout = Parse(Required(metadata, "oci-layout"), "layout");
        RejectUnknown(layout.RootElement, "layout", "imageLayoutVersion", "annotations");
        if (layout.RootElement.GetProperty("imageLayoutVersion").GetString() != "1.0.0")
            throw new InvalidDataException("Unsupported OCI layout version.");
        byte[] indexBytes = Required(metadata, "index.json");
        using JsonDocument index = Parse(indexBytes, "index");
        if (index.RootElement.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported index schema version.");
        RejectUnknown(index.RootElement, "index", "schemaVersion", "mediaType", "manifests", "annotations");
        if (expected.IndexDigest is not null && Sha256(indexBytes) != expected.IndexDigest)
            throw new InvalidDataException("Index identity mismatch.");
        JsonElement descriptors = index.RootElement.GetProperty("manifests");
        JsonElement selected = SelectManifest(descriptors, expected);
        RejectUnknown(selected, "manifest descriptor", "mediaType", "digest", "size", "platform", "annotations");
        string manifestDigest = StringProperty(selected, "digest");
        if (manifestDigest != expected.ManifestDigest)
            throw new InvalidDataException("Manifest identity mismatch.");
        if (StringProperty(selected, "mediaType") != "application/vnd.oci.image.manifest.v1+json")
            throw new InvalidDataException("Unsupported manifest media type.");
        byte[] manifestBytes = ReadBlob(blobs, manifestDigest, selected.GetProperty("size").GetInt64(), d);
        using JsonDocument manifest = Parse(manifestBytes, "manifest");
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Unsupported manifest schema version.");
        if (!manifest.RootElement.TryGetProperty("mediaType", out var manifestMediaType) || manifestMediaType.GetString() != "application/vnd.oci.image.manifest.v1+json")
            throw new InvalidDataException("Unsupported manifest media type.");
        RejectUnknown(manifest.RootElement, "manifest", "schemaVersion", "mediaType", "config", "layers", "annotations");
        JsonElement configDescriptor = manifest.RootElement.GetProperty("config");
        if (StringProperty(configDescriptor, "mediaType") != "application/vnd.oci.image.config.v1+json")
            throw new InvalidDataException("Unsupported config media type.");
        string configDigest = StringProperty(configDescriptor, "digest");
        if (configDigest != expected.ConfigDigest)
            throw new InvalidDataException("Config identity mismatch.");
        byte[] configBytes = ReadBlob(blobs, configDigest, configDescriptor.GetProperty("size").GetInt64(), d);
        using JsonDocument config = Parse(configBytes, "config");
        RejectUnknown(config.RootElement, "config", "created", "architecture", "os", "config", "rootfs", "history", "variant", "os.version", "os.features");
        JsonElement root = config.RootElement;
        string platform = root.GetProperty("os").GetString() + "/" + root.GetProperty("architecture").GetString();
        if (platform != expected.Platform)
            throw new InvalidDataException("Platform mismatch.");
        var diffIds = root.GetProperty("rootfs").GetProperty("diff_ids").EnumerateArray().Select(x => x.GetString()!).ToImmutableArray();
        if (!diffIds.SequenceEqual(expected.DiffIds))
            throw new InvalidDataException("Expected diffID sequence mismatch.");
        string user = root.GetProperty("config").TryGetProperty("User", out var configUser) && configUser.ValueKind is JsonValueKind.String ? configUser.GetString()! : "";
        if (expected.ConfigUser is not null && user != expected.ConfigUser)
            throw new InvalidDataException("Config user mismatch.");
        var entrypoint = ReadEntrypoint(root.GetProperty("config"));
        if (expected.ConfigEntrypoint is not null && !entrypoint.SequenceEqual(expected.ConfigEntrypoint))
            throw new InvalidDataException("Config entrypoint mismatch.");
        var env = ReadEnvironment(root.GetProperty("config"));
        if (expected.ConfigEnvironment is not null && !env.OrderBy(x => x.Key).SequenceEqual(expected.ConfigEnvironment.OrderBy(x => x.Key)))
            throw new InvalidDataException("Config environment mismatch.");
        var ports = ReadPorts(root.GetProperty("config"));
        JsonElement layers = manifest.RootElement.GetProperty("layers");
        if (layers.GetArrayLength() != diffIds.Length || layers.GetArrayLength() > limits.MaxLayers)
            throw new InvalidDataException("Layer count mismatch or exceeds budget.");
        var merged = new SortedDictionary<string, ObservedFile>(StringComparer.Ordinal);
        long expandedTotal = 0;
        int totalMembers = 0;
        for (int i = 0; i < layers.GetArrayLength(); i++)
        {
            d.Check();
            JsonElement ld = layers[i];
            RejectUnknown(ld, "layer descriptor", "mediaType", "digest", "size", "annotations");
            if (StringProperty(ld, "mediaType") != "application/vnd.oci.image.layer.v1.tar+gzip")
                throw new InvalidDataException("Unsupported layer media type.");
            string digest = StringProperty(ld, "digest");
            Blob blob = RequireBlob(blobs, digest, ld.GetProperty("size").GetInt64());
            string actual;
            using FileStream input = File.OpenRead(blob.Path);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using var hashing = new HashingStream(gzip, limits.MaxExpandedLayerBytes, d);
            using var preflight = new TarBudgetReadStream(hashing, limits.MaxExpandedLayerBytes, limits.MaxLayerEntryBytes, limits.MaxLayerMembers);
            using var tr = new TarReader(preflight, true);
            int members = 0;
            TarEntry? member;
            while ((member = tr.GetNextEntry()) is not null)
            {
                d.Check();
                if (++members > limits.MaxLayerMembers || ++totalMembers > limits.MaxTotalMembers)
                    throw new InvalidDataException("Layer member budget exceeded.");
                string item = SafePath(member.Name.TrimEnd('/'));
                d.AccountMetadata(item, member.LinkName);
                string leaf = item[(item.LastIndexOf('/') + 1)..];
                if (leaf == ".wh..wh..opq")
                {
                    RemoveLower(merged, Parent(item), i, selectedState);
                    continue;
                }

                if (leaf.StartsWith(".wh.", StringComparison.Ordinal))
                {
                    if (leaf.Length == 4)
                        throw new InvalidDataException("Malformed whiteout.");
                    RemoveLower(merged, Join(Parent(item), leaf[4..]), i, selectedState);
                    continue;
                }

                string kind = member.EntryType switch
                {
                    TarEntryType.RegularFile or TarEntryType.V7RegularFile => "file",
                    TarEntryType.Directory => "directory",
                    TarEntryType.SymbolicLink => "symlink",
                    TarEntryType.HardLink => "hardlink",
                    _ => throw new InvalidDataException("Unsupported special node.")
                };
                if (kind != "directory")
                    RemovePath(merged, item, selectedState);
                else if (merged.TryGetValue(item, out var old) && old.Type != "directory")
                    RemovePath(merged, item, selectedState);
                byte[]? payload = null;
                string hash = kind == "file" ? Hash(member.DataStream ?? Stream.Null, d, expected.PayloadPaths?.Contains(item) == true, limits.MaxPayloadBytes, out payload, selectedState?.CapturePath(item), new Action<int>(d.ConsumePayload), selectedState is null ? null : new Action<int>(selectedState.ConsumeSelected)) : "";
                merged[item] = new ObservedFile(item, kind, hash, member.Length, (int)(member.Mode & (UnixFileMode)0xFFF), member.Uid, member.Gid, kind is "symlink" or "hardlink" ? member.LinkName : null, i, payload);
                if (merged.Count > limits.MaxPaths)
                    throw new InvalidDataException("Merged path count exceeds budget.");
            }

            preflight.Finish();
            actual = "sha256:" + Convert.ToHexString(hashing.Hash()).ToLowerInvariant();
            expandedTotal = checked(expandedTotal + hashing.BytesRead);
            if (expandedTotal > limits.MaxExpandedTotalBytes || actual != diffIds[i])
                throw new InvalidDataException("Expanded layer diffID mismatch.");
        }

        ValidateExpectedFiles(merged, expected.SelectedFiles);
        selectedState?.Publish(merged);
        var status = merged.Values.Where(x => x.Path is "var/lib/dpkg/status" or "var/lib/dpkg/status.d" && x.TargetedPayload is not null).Select(x => x.Path).ToImmutableArray();
        return new ObservedOciImage(Sha256(indexBytes), manifestDigest, platform, configDigest, user, entrypoint, env.ToImmutableDictionary(StringComparer.Ordinal), ports, diffIds, merged.Values.ToImmutableArray(), status, limits, compressed, expandedTotal);
    }

    /// <summary>Validates selected paths, ancestors, type, raw hash, mode and ownership against the expected image.</summary>
    internal static void ValidateExpectedFiles(IReadOnlyDictionary<string, ObservedFile> observed, IReadOnlyDictionary<string, OciExpectedFile>? expected)
    {
        if (expected is null)
            return;
        foreach (var pair in expected)
        {
            string p = SafePath(pair.Key);
            foreach (string ancestor in Ancestors(p))
            {
                if (!observed.TryGetValue(ancestor, out var a) || a.Type != "directory")
                    throw new InvalidDataException("Selected path has a missing or linked ancestor.");
            }

            if (!observed.TryGetValue(p, out var o) || o.Type != pair.Value.Type || (pair.Value.Type == "file" && !StringComparer.OrdinalIgnoreCase.Equals(o.Sha256, pair.Value.Sha256)) || (pair.Value.Mode is int m && o.Mode != m) || (pair.Value.Uid is long u && o.Uid != u) || (pair.Value.Gid is long g && o.Gid != g))
                throw new InvalidDataException("Selected file does not match expected observation.");
            if (o.Type is "symlink" or "hardlink")
                throw new InvalidDataException("Selected path cannot be a link.");
        }
    }

    private static IEnumerable<string> Ancestors(string p)
    {
        int slash = p.LastIndexOf('/');
        while (slash > 0)
        {
            p = p[..slash];
            yield return p;
            slash = p.LastIndexOf('/');
        }
    }

    private static JsonElement SelectManifest(JsonElement list, OciExpectedImage e)
    {
        JsonElement? found = null;
        foreach (var d in list.EnumerateArray())
        {
            if (StringProperty(d, "digest") == e.ManifestDigest)
            {
                if (found is not null)
                    throw new InvalidDataException("Duplicate expected manifest.");
                found = d;
            }
        }

        return found ?? throw new InvalidDataException("Expected platform manifest is absent.");
    }

    /// <summary>Reads the optional OCI environment array and rejects malformed entries.</summary>
    internal static Dictionary<string, string> ReadEnvironment(JsonElement c)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!c.TryGetProperty("Env", out var a) || a.ValueKind is JsonValueKind.Null)
            return result;
        if (a.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Environment must be an array.");
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String || x.GetString() is not string s)
                throw new InvalidDataException("Environment contains a non-string value.");
            int i = s.IndexOf('=');
            if (i <= 0)
                throw new InvalidDataException("Environment entry is malformed.");
            result[s[..i]] = s[(i + 1)..];
        }

        return result;
    }

    private static ImmutableArray<string> ReadEntrypoint(JsonElement c)
    {
        if (!c.TryGetProperty("Entrypoint", out var value) || value.ValueKind is JsonValueKind.Null)
            return ImmutableArray<string>.Empty;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Entrypoint must be an array.");
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string command)
                throw new InvalidDataException("Entrypoint contains a non-string value.");
            result.Add(command);
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<string> ReadPorts(JsonElement c)
    {
        if (!c.TryGetProperty("ExposedPorts", out var value) || value.ValueKind is JsonValueKind.Null)
            return ImmutableArray<string>.Empty;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Exposed ports must be an object.");
        return value.EnumerateObject().Select(x => x.Name).Order().ToImmutableArray();
    }

    /// <summary>Removes a lower-layer path subtree for a named or opaque whiteout.</summary>
    internal static void RemoveLower(SortedDictionary<string, ObservedFile> m, string victim, int layer, SelectionState? selectedState = null)
    {
        foreach (string k in m.Keys.Where(k => (victim.Length == 0 ? m[k].Layer < layer : (k == victim || k.StartsWith(victim + "/", StringComparison.Ordinal)) && m[k].Layer < layer)).ToArray())
        {
            selectedState?.Remove(k);
            m.Remove(k);
        }
    }

    /// <summary>Removes a path and all descendants from the merged image view.</summary>
    internal static void RemovePath(SortedDictionary<string, ObservedFile> m, string p, SelectionState? selectedState = null)
    {
        foreach (string k in m.Keys.Where(k => k == p || k.StartsWith(p + "/", StringComparison.Ordinal)).ToArray())
        {
            selectedState?.Remove(k);
            m.Remove(k);
        }
    }

    private static string Parent(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..i];
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
    /// <summary>Validates and canonicalizes one OCI archive path without aliasing.</summary>
    internal static string SafePath(string p)
    {
        if (string.IsNullOrEmpty(p) || p.Length > 4096 || p.StartsWith('/') || p.Contains('\\') || p.Split('/').Any(x => x.Length == 0 || x == "." || x == ".." || x.Any(char.IsControl)))
            throw new InvalidDataException("Unsafe archive path: " + p);
        string n = p.Normalize(NormalizationForm.FormC);
        if (n != p)
            throw new InvalidDataException("Unicode alias path: " + p);
        return n;
    }

    private byte[] ReadBlob(Dictionary<string, Blob> b, string d, long size, Deadline x)
    {
        return ReadBoundedBlob(RequireBlob(b, d, size), x);
    }

    private byte[] ReadBoundedBlob(Blob b, Deadline d)
    {
        if (b.Length > limits.MaxJsonBytes)
            throw new InvalidDataException("Metadata blob exceeds bound.");
        using var s = File.OpenRead(b.Path);
        return ReadBounded(s, checked((int)b.Length), d);
    }

    private static Blob RequireBlob(Dictionary<string, Blob> b, string d, long size) => b.TryGetValue(d, out var x) && x.Length == size ? x : throw new InvalidDataException("Missing blob or descriptor size mismatch.");
    // Internal stream boundary permits independent short/growing-input and digest controls.
    internal static void Spool(Stream input, string path, long expected, string digest, Deadline d)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var o = File.Create(path);
        byte[] buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = input.Read(buf)) != 0)
        {
            d.Check();
            total = checked(total + n);
            if (total > expected)
                throw new InvalidDataException("Blob exceeds descriptor size.");
            h.AppendData(buf, 0, n);
            TemporaryDiskBudget.Account(n);
            o.Write(buf, 0, n);
        }

        if (total != expected || "sha256:" + Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant() != digest)
            throw new InvalidDataException("Blob digest mismatch.");
    }

    // The fixed-length metadata boundary is shared by real OCI parsing and stream-contract tests.
    internal static byte[] ReadBounded(Stream s, int length, Deadline d)
    {
        var b = new byte[length];
        int o = 0;
        while (o < b.Length)
        {
            d.Check();
            int n = s.Read(b, o, b.Length - o);
            if (n == 0)
                throw new EndOfStreamException();
            o += n;
        }

        if (s.ReadByte() != -1)
            throw new InvalidDataException("Bounded content has trailing bytes.");
        return b;
    }

    private static string Hash(Stream input, Deadline d, bool capture, long max, out byte[]? payload, string? outputPath = null, Action<int>? targetedAccount = null, Action<int>? selectedAccount = null)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        MemoryStream? copy = capture ? new MemoryStream() : null;
        try
        {
            byte[] b = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(b)) != 0)
            {
                d.Check();
                total = checked(total + n);
                if (capture && total > max)
                    throw new InvalidDataException("Targeted payload exceeds bound.");
                h.AppendData(b, 0, n);
                copy?.Write(b, 0, n);
                if (capture)
                    targetedAccount?.Invoke(n);
                if (outputPath is not null)
                    selectedAccount?.Invoke(n);
                if (outputPath is not null)
                {
                    using var output = File.Open(outputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    TemporaryDiskBudget.Account(n);
                    output.Write(b, 0, n);
                }
            }

            payload = copy?.ToArray();
            return "sha256:" + Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            copy?.Dispose();
        }
    }

    private static byte[] HashFile(string path)
    {
        using var input = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] b = new byte[81920];
        int n;
        while ((n = input.Read(b)) != 0)
            hash.AppendData(b, 0, n);
        return hash.GetHashAndReset();
    }

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonDocument Parse(byte[] b, string n)
    {
        try
        {
            var reader = new Utf8JsonReader(b, true, default);
            ValidateJsonShape(ref reader);
            return JsonDocument.Parse(b, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 128 });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid " + n + " JSON.");
        }
    }

    private static void RejectUnknown(JsonElement e, string kind, params string[] allowed)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unknown " + kind + " schema.");
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (!set.Contains(p.Name))
                throw new InvalidDataException("Unknown " + kind + " property.");
    }

    private static void ValidateJsonShape(ref Utf8JsonReader reader)
    {
        var scopes = new Stack<HashSet<string>>(16);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                scopes.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && scopes.Count > 0 && !scopes.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate JSON property.");
        }
    }

    private static byte[] Required(Dictionary<string, byte[]> m, string n) => m.TryGetValue(n, out var b) ? b : throw new InvalidDataException("Missing " + n + ".");
    private static string StringProperty(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is string s ? s : throw new InvalidDataException("Missing string property.");
    private sealed record Blob(string Path, long Length);
    internal sealed class SelectionState
    {
        private readonly string output;
        private readonly string scratch;
        private readonly HashSet<string> paths;
        private readonly Dictionary<string, string> staged = new(StringComparer.Ordinal);
        private readonly long maxSelectedOutput;
        private long selectedOutput;
        private SelectionState(string output, string scratch, HashSet<string> paths, long maxSelectedOutput)
        {
            this.output = output;
            this.scratch = scratch;
            this.paths = paths;
            this.maxSelectedOutput = maxSelectedOutput;
        }

        internal static SelectionState Create(OciReadSelection s, OciReaderLimits limits)
        {
            if (Directory.Exists(s.AbsentOutputDirectory) || File.Exists(s.AbsentOutputDirectory))
                throw new InvalidDataException("Selected output directory must be absent.");
            string output = Path.GetFullPath(s.AbsentOutputDirectory);
            var paths = new HashSet<string>(s.Paths.Select(SafePath), StringComparer.Ordinal);
            if (paths.Count == 0 || paths.Any(p => p.Length == 0))
                throw new InvalidDataException("Selected path is empty.");
            if (paths.Count > limits.MaxPaths)
                throw new InvalidDataException("Selected path count exceeds budget.");
            return new SelectionState(output, Path.Combine(Path.GetTempPath(), "forge-oci-selected-" + Guid.NewGuid().ToString("N")), paths, limits.MaxSelectedOutputBytes);
        }

        internal string? CapturePath(string path)
        {
            if (!paths.Contains(path))
                return null;
            Directory.CreateDirectory(scratch);
            string p = Path.Combine(scratch, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant());
            staged[path] = p;
            File.WriteAllBytes(p, Array.Empty<byte>());
            return p;
        }

        internal void ConsumeSelected(int bytes)
        {
            selectedOutput = checked(selectedOutput + bytes);
            if (selectedOutput > maxSelectedOutput)
                throw new InvalidDataException("Selected output exceeds budget.");
        }

        internal void Remove(string path)
        {
            if (staged.Remove(path, out var p))
                File.Delete(p);
        }

        internal void Cleanup()
        {
            try
            {
                if (Directory.Exists(scratch))
                    Directory.Delete(scratch, true);
            }
            catch
            {
            }
        }

        internal void Publish(IReadOnlyDictionary<string, ObservedFile> merged)
        {
            string stage = output + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(stage);
                foreach (string path in paths)
                {
                    if (!merged.TryGetValue(path, out var item) || item.Type != "file" || !staged.TryGetValue(path, out var temp) || !StringComparer.OrdinalIgnoreCase.Equals(item.Sha256, "sha256:" + Convert.ToHexString(HashFile(temp)).ToLowerInvariant()))
                        throw new InvalidDataException("Selected output is missing, non-regular, or hash-mismatched.");
                    string target = Path.Combine(stage, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(temp, target);
                }

                Directory.Move(stage, output);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(stage))
                        Directory.Delete(stage, true);
                }
                catch
                {
                }

                throw;
            }
        }
    }

    internal sealed class Deadline
    {
        private readonly CancellationToken token;
        private readonly long maximumPayload;
        private readonly TimeSpan maximumDuration;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private long metadata, payload;
        internal Deadline(CancellationToken token, long maximumPayload, TimeSpan? maximumDuration = null)
        {
            this.token = token;
            this.maximumPayload = maximumPayload;
            this.maximumDuration = maximumDuration ?? TimeSpan.FromMinutes(5);
        }

        internal void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > maximumDuration)
                throw new TimeoutException("OCI read deadline exceeded.");
        }

        // Conservative reservation includes string copies, dictionaries, records, sorting and JSON.
        // Charge every observed member, including superseded paths, before creating its record.
        internal void AccountMetadata(string path, string? link = null)
        {
            metadata = checked(metadata + 2048 + 16L * (path.Length + (link?.Length ?? 0)));
            if (metadata > 384L * 1024 * 1024)
                throw new InvalidDataException("OCI metadata memory reservation exceeded.");
        }

        internal void ConsumePayload(int bytes)
        {
            payload = checked(payload + bytes);
            if (payload > maximumPayload)
                throw new InvalidDataException("Targeted payload aggregate exceeds budget.");
        }
    }

    internal sealed class HashingStream : Stream
    {
        private readonly Stream inner;
        private readonly long max;
        private readonly Deadline deadline;
        private readonly IncrementalHash h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long BytesRead { get; private set; }

        internal HashingStream(Stream inner, long max, Deadline deadline)
        {
            (this.inner, this.max, this.deadline) = (inner, max, deadline);
        }

        internal byte[] Hash() => h.GetHashAndReset();
        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
        public override int Read(Span<byte> b)
        {
            int n = inner.Read(b);
            if (n > 0)
            {
                deadline.Check();
                BytesRead = checked(BytesRead + n);
                if (BytesRead > max)
                    throw new InvalidDataException("Expanded layer exceeds budget.");
                h.AppendData(b[..n]);
            }

            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                h.Dispose();
            base.Dispose(disposing);
        }
    }
}
