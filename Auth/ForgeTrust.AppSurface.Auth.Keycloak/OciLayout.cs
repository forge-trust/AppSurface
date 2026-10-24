using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Copies a verified, finite OCI graph into an owned local builder input.</summary>
internal static class OciLayout
{
    internal static void VerifyUpstreamIndex(string path, string expectedIndex, string selectedManifest, string platform)
    {
        var raw = ReadSmall(path);
        Require(Digest(raw) == expectedIndex, "Upstream index digest differs.");
        using var json = StrictJson(raw);
        var root = json.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 2, "Unsupported index schema.");
        var matches = root.GetProperty("manifests").EnumerateArray().Where(x =>
            x.GetProperty("digest").GetString() == selectedManifest).ToArray();
        Require(matches.Length == 1, "Selected manifest is absent or ambiguous in upstream index.");
        var selected = matches[0];
        var p = selected.GetProperty("platform");
        Require(p.GetProperty("os").GetString() + "/" + p.GetProperty("architecture").GetString() == platform,
            "Upstream index platform differs.");
        Require(selected.GetProperty("mediaType").GetString() == "application/vnd.oci.image.manifest.v1+json",
            "Unsupported selected manifest media type.");
    }

    // The caller first verifies the archive's complete ordered rootfs with OciReader. This
    // second pass copies only graph members, rehashing the bytes actually supplied to BuildKit.
    internal static void CopyVerifiedGraph(string archive, string manifestDigest, string configDigest, string absentOutput, CancellationToken token)
    {
        Require(!Path.Exists(absentOutput), "OCI output must be absent.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(absentOutput))!;
        RequireNoLinks(parent);
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".oci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stage, "blobs", "sha256"));
        try
        {
            var descriptors = new Dictionary<string, long>(StringComparer.Ordinal);
            byte[]? manifestBytes = null;
            // Manifest is bounded before JSON allocation. No path from the input archive is extracted.
            using (var input = File.OpenRead(archive))
            using (var bound = new TarBudgetReadStream(input, 1024L * 1024 * 1024, 1024L * 1024 * 1024, 65536))
            using (var tar = new TarReader(bound))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Name == "blobs/sha256/" + manifestDigest[7..])
                    {
                        Require(manifestBytes is null && entry.Length <= 1024 * 1024 && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile,
                            "Manifest is oversized or ambiguous.");
                        manifestBytes = new byte[checked((int)entry.Length)];
                        entry.DataStream!.ReadExactly(manifestBytes);
                    }
                }
                bound.Finish();
            }
            Require(manifestBytes is not null && Digest(manifestBytes) == manifestDigest, "Manifest bytes differ.");
            using (var json = StrictJson(manifestBytes!))
            {
                var root = json.RootElement;
                var config = root.GetProperty("config");
                Require(config.GetProperty("digest").GetString() == configDigest, "Config identity differs.");
                descriptors.Add(configDigest, config.GetProperty("size").GetInt64());
                foreach (var layer in root.GetProperty("layers").EnumerateArray())
                    Require(descriptors.TryAdd(layer.GetProperty("digest").GetString()!, layer.GetProperty("size").GetInt64()), "Repeated layer identity unsupported.");
                descriptors.Add(manifestDigest, manifestBytes!.LongLength);
            }
            var remaining = descriptors.Keys.ToHashSet(StringComparer.Ordinal);
            using (var input = File.OpenRead(archive))
            using (var bound = new TarBudgetReadStream(input, 1024L * 1024 * 1024, 1024L * 1024 * 1024, 65536))
            using (var tar = new TarReader(bound))
            {
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entry.Name.StartsWith("blobs/sha256/", StringComparison.Ordinal)) continue;
                    var digest = "sha256:" + entry.Name[13..];
                    if (!descriptors.TryGetValue(digest, out var length)) continue;
                    Require(remaining.Remove(digest) && entry.Length == length && length >= 0 && length <= 1024L * 1024 * 1024,
                        "Graph member is missing, repeated, or oversized.");
                    var hex = digest[7..];
                    Require(hex.Length == 64 && hex.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)), "Noncanonical graph digest.");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var output = new FileStream(Path.Combine(stage, "blobs", "sha256", hex), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536]; long total = 0; int read;
                    while ((read = entry.DataStream!.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested(); total = checked(total + read);
                        Require(total <= length, "Graph member grew."); hash.AppendData(buffer, 0, read); output.Write(buffer, 0, read);
                    }
                    Require(total == length && Convert.ToHexStringLower(hash.GetHashAndReset()) == hex, "Graph member digest differs.");
                    output.Flush(true);
                }
                bound.Finish();
            }
            Require(remaining.Count == 0, "Graph closure is incomplete.");
            File.WriteAllText(Path.Combine(stage, "oci-layout"), "{\"imageLayoutVersion\":\"1.0.0\"}\n", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(stage, "index.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                manifests = new[] { new { mediaType = "application/vnd.oci.image.manifest.v1+json", digest = manifestDigest, size = manifestBytes!.Length } }
            }));
            token.ThrowIfCancellationRequested(); RequireNoLinks(parent);
            Directory.Move(stage, absentOutput);
        }
        finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal static JsonDocument StrictJson(byte[] bytes)
    {
        Require(bytes.Length <= 1024 * 1024, "JSON exceeds profile limit.");
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) scopes.Push(new(StringComparer.Ordinal));
            if (reader.TokenType == JsonTokenType.EndObject) scopes.Pop();
            if (reader.TokenType == JsonTokenType.PropertyName) Require(scopes.Peek().Add(reader.GetString()!), "Repeated JSON property.");
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }
    private static byte[] ReadSmall(string path)
    {
        using var file = File.OpenRead(path); Require(file.Length <= 1024 * 1024, "Index exceeds profile limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void RequireNoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            Require(new FileInfo(current).LinkTarget is null && new DirectoryInfo(current).LinkTarget is null, "A local path contains a link.");
            current = Path.GetDirectoryName(current);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
