using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

/// <summary>
/// Small real OCI fixture for capture, materialization, and inspection seam tests.
/// Every declared material is host-bound to a file under <see cref="Root"/>.
/// </summary>
internal sealed class TinyImageFixture : IDisposable
{
    private readonly string root;

    private TinyImageFixture(
        string root,
        KeycloakSecurityRecipe recipe,
        KeycloakMaterialTrustPolicy policy,
        KeycloakCaptureInputs bindings,
        KeycloakImageIdentity image,
        ImmutableDictionary<string, string> materialPaths)
    {
        (this.root, Recipe, Policy, Bindings, Image, MaterialPaths) = (root, recipe, policy, bindings, image, materialPaths);
    }

    internal string Root => root;
    internal KeycloakSecurityRecipe Recipe { get; }
    internal KeycloakMaterialTrustPolicy Policy { get; }
    internal KeycloakCaptureInputs Bindings { get; }
    internal KeycloakImageIdentity Image { get; }
    internal KeycloakInspectionImage InspectionImage => new(Image.PlatformManifest, Image.ConfigDigest!, Image.Platform, Image.DiffIds);
    internal string ArchivePath => MaterialPaths[Image.ArchiveMaterialId!];
    internal ImmutableDictionary<string, string> MaterialPaths { get; }

    internal static TinyImageFixture Create(bool includeOutside = false)
    {
        var root = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-tiny-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var layerTar = LayerTar(includeOutside);
            var layerDiffId = Sha256(layerTar);
            var layerGzip = Gzip(layerTar);
            var layerDigest = Sha256(layerGzip);
            var config = JsonBytes(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("architecture", "amd64"); writer.WriteString("os", "linux");
                writer.WritePropertyName("config"); writer.WriteStartObject();
                writer.WriteString("User", "1000:0");
                writer.WritePropertyName("Entrypoint"); writer.WriteStartArray(); writer.WriteStringValue("/opt/keycloak/bin/kc.sh"); writer.WriteEndArray();
                writer.WritePropertyName("Env"); writer.WriteStartArray(); writer.WriteStringValue("KC_RUN_IN_CONTAINER=true"); writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WritePropertyName("rootfs"); writer.WriteStartObject(); writer.WriteString("type", "layers"); writer.WritePropertyName("diff_ids"); writer.WriteStartArray(); writer.WriteStringValue(layerDiffId); writer.WriteEndArray(); writer.WriteEndObject();
                writer.WritePropertyName("history"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WriteEndObject();
            });
            var configDigest = Sha256(config);
            var manifest = JsonBytes(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("mediaType", "application/vnd.oci.image.manifest.v1+json");
                writer.WritePropertyName("config"); Descriptor(writer, "application/vnd.oci.image.config.v1+json", configDigest, config.Length);
                writer.WritePropertyName("layers"); writer.WriteStartArray(); Descriptor(writer, "application/vnd.oci.image.layer.v1.tar+gzip", layerDigest, layerGzip.Length); writer.WriteEndArray();
                writer.WriteEndObject();
            });
            var manifestDigest = Sha256(manifest);
            var index = JsonBytes(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("mediaType", "application/vnd.oci.image.index.v1+json");
                writer.WritePropertyName("manifests"); writer.WriteStartArray();
                writer.WriteStartObject(); writer.WriteString("mediaType", "application/vnd.oci.image.manifest.v1+json"); writer.WriteString("digest", manifestDigest); writer.WriteNumber("size", manifest.Length);
                writer.WritePropertyName("platform"); writer.WriteStartObject(); writer.WriteString("architecture", "amd64"); writer.WriteString("os", "linux"); writer.WriteEndObject(); writer.WriteEndObject();
                writer.WriteEndArray(); writer.WriteEndObject();
            });
            var indexDigest = Sha256(index);
            var layout = Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}");
            var archive = OuterArchive(layout, index, manifest, config, layerGzip, configDigest, manifestDigest, layerDigest);

            var image = KeycloakImageIdentity.Create(indexDigest, manifestDigest, "linux/amd64") with
            {
                IndexMaterialId = "index",
                ArchiveMaterialId = "archive-bytes",
                ConfigDigest = configDigest,
                DiffIds = [layerDiffId]
            };
            var frontend = new OciIdentity("frontend", indexDigest) with
            {
                Id = "frontend",
                Platform = "linux/amd64",
                ArchiveMaterialId = "frontend-bytes",
                PlatformManifestDigest = manifestDigest,
                ConfigDigest = configDigest,
                IndexMaterialId = "frontend-index",
                DiffIds = [layerDiffId]
            };
            var baseRecipe = TestRecipes.Recipe(KeycloakRecipeMode.ThemeOnly, []) with
            {
                SourceImage = image,
                RuntimeFrontendOciIdentities = [frontend]
            };
            var bytesById = baseRecipe.MaterialReferences.ToImmutableDictionary(
                material => material.Id,
                material => material.Id switch
                {
                    "index" or "frontend-index" => index,
                    "archive-bytes" or "frontend-bytes" => archive,
                    _ => Encoding.UTF8.GetBytes(material.Id)
                }, StringComparer.Ordinal);
            var recipe = baseRecipe with
            {
                CapturedHostInputs = bytesById.Keys.ToImmutableArray(),
                MaterialReferences = baseRecipe.MaterialReferences.Select(material => material with
                {
                    ExactHttpsUri = string.Empty,
                    Sha256 = Hex(bytesById[material.Id]),
                    MaxBytes = bytesById[material.Id].LongLength,
                    CapturedHostInput = true
                }).ToImmutableArray()
            };
            var materialPaths = bytesById.ToImmutableDictionary(
                item => item.Key,
                item => Path.Combine(root, item.Key), StringComparer.Ordinal);
            foreach (var item in bytesById) File.WriteAllBytes(materialPaths[item.Key], item.Value);
            var policy = TestRecipes.Policy(false) with
            {
                MaterialSha256ByIdentity = recipe.MaterialReferences.ToImmutableDictionary(x => x.Id, x => x.Sha256, StringComparer.Ordinal)
            };
            var bindings = KeycloakCaptureInputs.Create(
                KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)),
                recipe.MaterialReferences.Select(material => new KeycloakLocalInputBinding(material.Id, materialPaths[material.Id], material.Sha256, material.MaxBytes)));
            return new(root, recipe, policy, bindings, image, materialPaths);
        }
        catch { try { Directory.Delete(root, true); } catch { } throw; }
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { } }

    private static byte[] LayerTar(bool includeOutside)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            if (includeOutside)
            {
                DirectoryEntry(writer, "etc");
                FileEntry(writer, "etc/reviewed.txt", Encoding.UTF8.GetBytes("baseline outside app\n"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "etc/reviewed-link") { LinkName = "reviewed.txt", Mode = (UnixFileMode)511, ModificationTime = DateTimeOffset.UnixEpoch });
            }
            DirectoryEntry(writer, "opt"); DirectoryEntry(writer, "opt/keycloak"); DirectoryEntry(writer, "opt/keycloak/bin");
            DirectoryEntry(writer, "opt/keycloak/themes"); DirectoryEntry(writer, "opt/keycloak/themes/keycloak.v2"); DirectoryEntry(writer, "opt/keycloak/themes/keycloak.v2/login");
            FileEntry(writer, "opt/keycloak/bin/kc.sh", Encoding.UTF8.GetBytes("#!/bin/sh\n"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            FileEntry(writer, "opt/keycloak/themes/keycloak.v2/login/login.ftl", Encoding.UTF8.GetBytes("tiny-theme\n"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        return output.ToArray();
    }

    private static byte[] OuterArchive(byte[] layout, byte[] index, byte[] manifest, byte[] config, byte[] layer, string configDigest, string manifestDigest, string layerDigest)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            FileEntry(writer, "oci-layout", layout); FileEntry(writer, "index.json", index);
            FileEntry(writer, "blobs/sha256/" + manifestDigest[7..], manifest); FileEntry(writer, "blobs/sha256/" + configDigest[7..], config); FileEntry(writer, "blobs/sha256/" + layerDigest[7..], layer);
        }
        return output.ToArray();
    }

    private static void Descriptor(Utf8JsonWriter writer, string mediaType, string digest, int size)
    {
        writer.WriteStartObject(); writer.WriteString("mediaType", mediaType); writer.WriteString("digest", digest); writer.WriteNumber("size", size); writer.WriteEndObject();
    }

    private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) { write(writer); writer.Flush(); }
        return output.ToArray();
    }

    private static void DirectoryEntry(TarWriter writer, string path)
        => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, path) { ModificationTime = DateTimeOffset.UnixEpoch, Mode = (UnixFileMode)493, Uid = 1000, Gid = 0 });

    private static void FileEntry(TarWriter writer, string path, byte[] bytes, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
        => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(bytes), ModificationTime = DateTimeOffset.UnixEpoch, Mode = mode, Uid = 1000, Gid = 0 });

    private static byte[] Gzip(byte[] bytes) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes); return output.ToArray(); }
    private static string Sha256(byte[] bytes) => "sha256:" + Hex(bytes);
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
