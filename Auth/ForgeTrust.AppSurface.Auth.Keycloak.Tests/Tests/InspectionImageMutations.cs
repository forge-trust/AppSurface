using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

public enum InspectionImageMutation
{
    ExtraOutsideApplicationTree,
    ExtraInsideApplicationTree,
    ChangedKeycloakFile
}

/// <summary>Builds disposable OCI archives whose graph and layer digests are recomputed after one deliberate mutation.</summary>
internal sealed class InspectionImageMutationArchive : IDisposable
{
    private InspectionImageMutationArchive(string root, KeycloakInspectionImage image, string archivePath)
        => (Root, Image, ArchivePath) = (root, image, archivePath);

    internal string Root { get; }
    internal KeycloakInspectionImage Image { get; }
    internal string ArchivePath { get; }

    internal static InspectionImageMutationArchive Create(TinyImageFixture fixture, InspectionImageMutation mutation)
    {
        var root = Path.Combine(fixture.Root, "inspection-mutation-" + mutation + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = ReadArchive(File.ReadAllBytes(fixture.ArchivePath));
            var manifestDigest = source.Index["manifests"]![0]! ["digest"]!.GetValue<string>();
            var manifest = source.Manifest;
            var configDigest = manifest["config"]!["digest"]!.GetValue<string>();
            var layerDigest = manifest["layers"]![0]! ["digest"]!.GetValue<string>();
            var layer = MutateLayer(Gunzip(source.Blobs[layerDigest]), mutation);
            var layerDiffId = Sha256(layer);
            var layerGzip = Gzip(layer);

            var config = JsonNode.Parse(source.Blobs[configDigest])!.AsObject();
            config["rootfs"]!["diff_ids"]![0] = layerDiffId;
            var configBytes = JsonBytes(config);
            var newConfigDigest = Sha256(configBytes);

            var manifestObject = manifest.DeepClone().AsObject();
            manifestObject["config"]!["digest"] = newConfigDigest;
            manifestObject["config"]!["size"] = configBytes.Length;
            manifestObject["layers"]![0]!["digest"] = Sha256(layerGzip);
            manifestObject["layers"]![0]!["size"] = layerGzip.Length;
            var manifestBytes = JsonBytes(manifestObject);
            var newManifestDigest = Sha256(manifestBytes);

            var index = source.Index.DeepClone().AsObject();
            index["manifests"]![0]!["digest"] = newManifestDigest;
            index["manifests"]![0]!["size"] = manifestBytes.Length;
            var newLayerDigest = Sha256(layerGzip);
            var archive = OuterArchive(source.Layout, index, manifestObject, config, layerGzip,
                newConfigDigest, newManifestDigest, newLayerDigest);
            var archivePath = Path.Combine(root, "mutated.oci.tar");
            File.WriteAllBytes(archivePath, archive);
            return new(root, new(newManifestDigest, newConfigDigest, fixture.InspectionImage.Platform, [layerDiffId]), archivePath);
        }
        catch
        {
            try { Directory.Delete(root, true); } catch { }
            throw;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (DirectoryNotFoundException) { }
    }

    private static ArchiveParts ReadArchive(byte[] archive)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var input = new MemoryStream(archive);
        using var reader = new TarReader(input);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            using var bytes = new MemoryStream();
            entry.DataStream?.CopyTo(bytes);
            entries[entry.Name] = bytes.ToArray();
        }

        var index = JsonNode.Parse(entries["index.json"])!.AsObject();
        var rawBlobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in entries.Where(x => x.Key.StartsWith("blobs/sha256/", StringComparison.Ordinal)))
            rawBlobs["sha256:" + item.Key["blobs/sha256/".Length..]] = item.Value;
        var manifestDigest = index["manifests"]![0]! ["digest"]!.GetValue<string>();
        return new(entries["oci-layout"], index, JsonNode.Parse(rawBlobs[manifestDigest])!.AsObject(), rawBlobs);
    }

    private static byte[] MutateLayer(byte[] layer, InspectionImageMutation mutation)
    {
        var entries = new List<TarEntrySnapshot>();
        using (var input = new MemoryStream(layer))
        using (var reader = new TarReader(input))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                using var data = new MemoryStream();
                entry.DataStream?.CopyTo(data);
                entries.Add(new(entry.EntryType, entry.Name, entry.LinkName, data.ToArray(), entry.Mode, entry.Uid, entry.Gid));
            }
        }

        if (mutation == InspectionImageMutation.ChangedKeycloakFile)
        {
            var target = entries.FindIndex(x => x.Name == "opt/keycloak/themes/keycloak.v2/login/login.ftl");
            entries[target] = entries[target] with { Data = Encoding.UTF8.GetBytes("tampered-theme\n") };
        }
        else
        {
            if (mutation == InspectionImageMutation.ExtraInsideApplicationTree)
                entries.Add(new(TarEntryType.RegularFile, "opt/keycloak/extra.txt", null, Encoding.UTF8.GetBytes("undeclared\n"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, 1000, 0));
            else
            {
                entries.Add(new(TarEntryType.Directory, "etc", null, [], (UnixFileMode)493, 0, 0));
                entries.Add(new(TarEntryType.RegularFile, "etc/undeclared", null, Encoding.UTF8.GetBytes("undeclared\n"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, 0, 0));
            }
        }

        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var tar = item.Type switch
                {
                    TarEntryType.Directory => new PaxTarEntry(TarEntryType.Directory, item.Name),
                    TarEntryType.SymbolicLink => new PaxTarEntry(TarEntryType.SymbolicLink, item.Name) { LinkName = item.LinkName ?? string.Empty },
                    _ => new PaxTarEntry(TarEntryType.RegularFile, item.Name) { DataStream = new MemoryStream(item.Data) }
                };
                tar.Mode = item.Mode; tar.Uid = checked((int)item.Uid); tar.Gid = checked((int)item.Gid); tar.ModificationTime = DateTimeOffset.UnixEpoch;
                writer.WriteEntry(tar);
            }
        }
        return output.ToArray();
    }

    private static byte[] OuterArchive(byte[] layout, JsonObject index, JsonObject manifest, JsonObject config, byte[] layer, string configDigest, string manifestDigest, string layerDigest)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            FileEntry(writer, "oci-layout", layout); FileEntry(writer, "index.json", JsonBytes(index));
            FileEntry(writer, "blobs/sha256/" + manifestDigest[7..], JsonBytes(manifest));
            FileEntry(writer, "blobs/sha256/" + configDigest[7..], JsonBytes(config));
            FileEntry(writer, "blobs/sha256/" + layerDigest[7..], layer);
        }
        return output.ToArray();
    }

    private static byte[] JsonBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    private static byte[] Gunzip(byte[] bytes) { using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray(); }
    private static byte[] Gzip(byte[] bytes) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes); return output.ToArray(); }
    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private static void FileEntry(TarWriter writer, string path, byte[] bytes) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, ModificationTime = DateTimeOffset.UnixEpoch });

    private sealed record ArchiveParts(byte[] Layout, JsonObject Index, JsonObject Manifest, Dictionary<string, byte[]> Blobs);
    private sealed record TarEntrySnapshot(TarEntryType Type, string Name, string? LinkName, byte[] Data, UnixFileMode Mode, long Uid, long Gid);
}
