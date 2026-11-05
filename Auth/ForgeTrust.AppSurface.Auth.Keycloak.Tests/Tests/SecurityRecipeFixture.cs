using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

/// <summary>
/// Test-owned runtime security fixture with real bounded OCI, signed-index, and DEB bytes.
/// The Java and OCI contents are deliberately tiny synthetic test material; they do not
/// claim to be a Keycloak distribution or a Docker runtime.
/// </summary>
internal sealed class SecurityRecipeFixture : IDisposable
{
    private SecurityRecipeFixture(
        string root,
        KeycloakSecurityRecipe recipe,
        KeycloakMaterialTrustPolicy policy,
        KeycloakCaptureInputs bindings,
        KeycloakInspectionImage finalImage,
        ImmutableDictionary<string, string> materialPaths,
        string finalArchivePath)
    {
        (Root, Recipe, Policy, Bindings, FinalImage, MaterialPaths, FinalArchivePath) =
                (root, recipe, policy, bindings, finalImage, materialPaths, finalArchivePath);
    }

    internal string Root { get; }
    internal KeycloakSecurityRecipe Recipe { get; }
    internal KeycloakMaterialTrustPolicy Policy { get; }
    internal KeycloakCaptureInputs Bindings { get; }
    internal KeycloakInspectionImage FinalImage { get; }
    internal ImmutableDictionary<string, string> MaterialPaths { get; }
    internal string ArchivePath => MaterialPaths[Recipe.SourceImage.ArchiveMaterialId!];
    internal string FinalArchivePath { get; }

    internal static SecurityRecipeFixture Create(bool complete = false)
    {
        var root = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-security-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var extra = complete ? CompleteRecipeFixture.Create() : null;
            var oci = BuildOci(includeRemovedBaseline: true, extra);
            var final = BuildOci(includeRemovedBaseline: false, extra);
            var packageBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["openssl"] = BuildDeb("openssl", "3.0.0"),
                ["openssl-provider-legacy"] = BuildDeb("openssl-provider-legacy", "3.0.0"),
                ["libssl3t64"] = BuildDeb("libssl3t64", "3.0.0")
            };
            var paragraphs = packageBytes.Select(item => PackageParagraph(item.Key, "3.0.0", item.Value)).ToArray();
            var signed = SignedRepositoryFixture.Create(packageIndexText: string.Join("\n", paragraphs));

            var bytesById = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["source-index"] = oci.Index,
                ["source-archive"] = oci.Archive,
                ["runtime-index"] = oci.Index,
                ["runtime-archive"] = oci.Archive,
                ["frontend-index"] = oci.Index,
                ["frontend-archive"] = oci.Archive,
                ["final-index"] = final.Index,
                ["final-archive"] = final.Archive,
                ["release"] = signed.ReleaseBytes,
                ["keyring"] = signed.KeyringBytes,
                ["package-index"] = signed.IndexBytes,
                ["openssl"] = packageBytes["openssl"],
                ["openssl-provider-legacy"] = packageBytes["openssl-provider-legacy"],
                ["libssl3t64"] = packageBytes["libssl3t64"]
            };
            if (extra is not null)
                foreach (var item in extra.Paths) bytesById.Add(item.Key, File.ReadAllBytes(item.Value));
            var materialPaths = bytesById.ToImmutableDictionary(
                item => item.Key, item => Path.Combine(root, item.Key), StringComparer.Ordinal);
            foreach (var item in bytesById) File.WriteAllBytes(materialPaths[item.Key], item.Value);

            var remoteUri = "https://snapshot.example.test/path/";
            var materials = bytesById.Select(item =>
                item.Key is "release" or "keyring" or "package-index" or "openssl" or "openssl-provider-legacy" or "libssl3t64"
                    ? new KeycloakMaterialReference(item.Key, remoteUri, Hex(item.Value), item.Value.LongLength)
                    : new KeycloakMaterialReference(item.Key, string.Empty, Hex(item.Value), item.Value.LongLength) { CapturedHostInput = true }).ToImmutableArray();
            var image = KeycloakImageIdentity.Create(oci.IndexDigest, oci.ManifestDigest, "linux/amd64") with
            {
                IndexMaterialId = "source-index",
                ArchiveMaterialId = "source-archive",
                ConfigDigest = oci.ConfigDigest,
                DiffIds = [oci.DiffId]
            };
            var runtime = new OciIdentity("runtime", oci.IndexDigest) with
            {
                Id = "runtime",
                Platform = "linux/amd64",
                ArchiveMaterialId = "runtime-archive",
                IndexMaterialId = "runtime-index",
                PlatformManifestDigest = oci.ManifestDigest,
                ConfigDigest = oci.ConfigDigest,
                DiffIds = [oci.DiffId]
            };
            var frontend = new OciIdentity("frontend", oci.IndexDigest) with
            {
                Id = "frontend",
                Platform = "linux/amd64",
                ArchiveMaterialId = "frontend-archive",
                IndexMaterialId = "frontend-index",
                PlatformManifestDigest = oci.ManifestDigest,
                ConfigDigest = oci.ConfigDigest,
                DiffIds = [oci.DiffId]
            };
            var profile = RuntimeProfile.Create("runtime", "1", "temurin-ubuntu", ["keycloak-26.7.2"]) with
            {
                RuntimeOciIdentities = [runtime],
                RepositorySnapshots = [new SignedRepositorySnapshot("ubuntu", remoteUri, Hex(signed.ReleaseBytes),
                    Convert.ToHexString(signed.SigningPair.PublicKey.GetFingerprint()), "resolute-updates", ["main"], ["amd64"])
                {
                    KeyringMaterialId = "keyring", PackageIndexMaterialIds = ImmutableDictionary<string, string>.Empty.Add("main/binary-amd64/Packages.gz", "package-index")
                }],
                RequiredPackages = packageBytes.Select(item => new RuntimePackageSpec(item.Key, "3.0.0", Hex(item.Value), "amd64") { MaterialId = item.Key }).ToImmutableArray(),
                RemovedPackages = [new("curl", "amd64", "8.5.0"), new("libcurl4t64", "amd64", "8.5.0")],
                ExpectedObservations = ["nonroot"]
            };
            var recipe = KeycloakSecurityRecipe.Create(1, image, [frontend],
                [new KeycloakOperation.RuntimeProfileOperation(profile)], KeycloakRecipeMode.Security, [], materials.Select(x => x.Id)) with
            {
                KeycloakVersion = "26.7.2",
                EntrypointPath = "/opt/keycloak/bin/kc.sh",
                SourceProfiles = ["keycloak-26.7.2"],
                MaterialReferences = materials,
                ResourceLimits = new(1L << 30, 65536, 1L << 30, 1L << 30, 300)
            };
            if (extra is not null)
            {
                var destination = "/opt/keycloak/lib/server.jar";
                var original = new ArtifactReference(new("com.example", "security-adapter", "1.0.0", null, "jar"), extra.Hashes["maven-source"], "", destination);
                var replacement = new ArtifactReference(original.Coordinate with { Version = "2.0.0" }, extra.Hashes["maven-replacement"], "", destination);
                var cli = new ArtifactCoordinate("org.keycloak", "keycloak-admin-cli", "26.7.2", null, "jar");
                var sourceModules = new Dictionary<string, string> { ["com.fasterxml.jackson.core:jackson-core:2.21.5::jar"] = extra.Hashes["jackson-core-source"], ["com.fasterxml.jackson.core:jackson-databind:2.21.5::jar"] = extra.Hashes["jackson-databind-source"] }.ToImmutableDictionary();
                var replacementModules = new Dictionary<string, string> { ["com.fasterxml.jackson.core:jackson-core:2.21.7::jar"] = extra.Hashes["jackson-core-replacement"], ["com.fasterxml.jackson.core:jackson-databind:2.21.7::jar"] = extra.Hashes["jackson-databind-replacement"] }.ToImmutableDictionary();
                var archive = new EmbeddedArchiveReplacement("archive", "1", "jackson", cli, cli,
                    ["com/fasterxml/jackson/core/", "com/fasterxml/jackson/databind/"], [],
                    ["META-INF/services/com.fasterxml.jackson.core.JsonFactory", "META-INF/services/com.fasterxml.jackson.core.ObjectCodec"])
                {
                    ModuleSet = new(new(cli, extra.Hashes["cli-source"], "", extra.CliDestination), new(cli, extra.Hashes["cli-repaired"], "", extra.CliDestination), extra.CliDestination, sourceModules, replacementModules)
                };
                recipe = recipe with
                {
                    Operations = [new KeycloakOperation.ArtifactReplacementGroupOperation(ArtifactReplacementGroup.Create("artifacts", [new(original, replacement)], [replacement.Coordinate])), new KeycloakOperation.RuntimeProfileOperation(profile), new KeycloakOperation.EmbeddedArchiveReplacementOperation(archive)],
                    TemplateBaseline = new(extra.TemplateArchivePath, extra.Hashes["template-baseline"], extra.TemplateEntry, extra.TemplateEntrySha256, "baseline-entry"),
                    EntrypointPath = "/opt/keycloak/bin/skoolit-entrypoint.sh",
                    ThemeInputs = [new("login", extra.Hashes["theme-input"], null, "theme-input") { LocalInputId = "theme-input", DestinationPath = "/opt/keycloak/themes/keycloak.v2/login/login.ftl" }, new("entrypoint", extra.Hashes["entrypoint"], null, "entrypoint") { LocalInputId = "entrypoint", DestinationPath = "/opt/keycloak/bin/skoolit-entrypoint.sh" }]
                };
            }
            var policy = KeycloakMaterialTrustPolicy.Create("fixture-v1", "fixture-owner-887", ["https://snapshot.example.test/"], [remoteUri], complete ? ["com.example:security-adapter:2.0.0::jar", "com.fasterxml.jackson.core:jackson-core:2.21.7::jar", "com.fasterxml.jackson.core:jackson-databind:2.21.7::jar"] : ["com.microsoft.sqlserver"],
                materials.Select(x => new KeyValuePair<string, string>(x.Id, x.Sha256)),
                [Convert.ToHexString(signed.SigningPair.PublicKey.GetFingerprint())], ["RSA/SHA512"], [], [Hex(signed.ReleaseBytes)]);
            var bindings = KeycloakCaptureInputs.Create(KeycloakSecurity.Hash(KeycloakCanonicalJson.Write(recipe)),
                materials.Select(x => new KeycloakLocalInputBinding(x.Id, materialPaths[x.Id], x.Sha256, x.MaxBytes)));
            return new(root, recipe, policy, bindings, new(final.ManifestDigest, final.ConfigDigest, "linux/amd64", [final.DiffId]), materialPaths, Path.Combine(root, "final-archive"));
        }
        catch { try { Directory.Delete(root, true); } catch { } throw; }
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (DirectoryNotFoundException) { } }

    private static string PackageParagraph(string name, string version, byte[] bytes) =>
        $"Package: {name}\nVersion: {version}\nArchitecture: amd64\nFilename: pool/main/{name[0]}/{name}_{version}_amd64.deb\nSize: {bytes.Length}\nSHA256: {Hex(bytes)}\nDepends: libc6 (>= 2.0)\n";

    private static byte[] BuildDeb(string name, string version)
    {
        var control = TarBytes(new Dictionary<string, byte[]> { ["control"] = Encoding.UTF8.GetBytes($"Package: {name}\nVersion: {version}\nArchitecture: amd64\n") });
        var data = TarBytes(new Dictionary<string, byte[]> { [$"usr/lib/{name}.so"] = Encoding.UTF8.GetBytes(name) });
        using var output = new MemoryStream(); output.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
        Member(output, "debian-binary", Encoding.ASCII.GetBytes("2.0\n")); Member(output, "control.tar.gz", Gzip(control)); Member(output, "data.tar.gz", Gzip(data));
        output.Position = 0; return output.ToArray();
    }

    private static void Member(Stream output, string name, byte[] bytes)
    {
        var header = new byte[60]; Encoding.ASCII.GetBytes((name + "/").PadRight(16)).CopyTo(header, 0); Encoding.ASCII.GetBytes("0           ").CopyTo(header, 16);
        Encoding.ASCII.GetBytes("0     ").CopyTo(header, 28); Encoding.ASCII.GetBytes("0     ").CopyTo(header, 34); Encoding.ASCII.GetBytes("100644  ").CopyTo(header, 40);
        Encoding.ASCII.GetBytes(bytes.Length.ToString().PadLeft(10)).CopyTo(header, 48); header[58] = 0x60; header[59] = 10; output.Write(header); output.Write(bytes); if ((bytes.Length & 1) != 0) output.WriteByte(10);
    }

    private static byte[] TarBytes(Dictionary<string, byte[]> files, HashSet<string>? directories = null)
    { using var output = new MemoryStream(); using (var writer = new TarWriter(output, TarEntryFormat.Pax, true)) foreach (var item in files) { var directory = directories?.Contains(item.Key) == true; writer.WriteEntry(directory ? new PaxTarEntry(TarEntryType.Directory, item.Key) { Mode = (UnixFileMode)493, Uid = 1000, Gid = 0 } : new PaxTarEntry(TarEntryType.RegularFile, item.Key) { DataStream = new MemoryStream(item.Value), Mode = item.Key.EndsWith("kc.sh", StringComparison.Ordinal) || item.Key.EndsWith("kcadm.sh", StringComparison.Ordinal) || item.Key.EndsWith("skoolit-entrypoint.sh", StringComparison.Ordinal) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, Uid = 1000, Gid = 0 }); } return output.ToArray(); }
    private static byte[] Gzip(byte[] bytes) { using var output = new MemoryStream(); using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(bytes); return output.ToArray(); }

    private static (byte[] Index, byte[] Archive, string IndexDigest, string ManifestDigest, string ConfigDigest, string DiffId) BuildOci(bool includeRemovedBaseline, CompleteRecipeFixture? extra = null)
    {
        var status = "Package: openssl\nVersion: 3.0.0\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: openssl-provider-legacy\nVersion: 3.0.0\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: libssl3t64\nVersion: 3.0.0\nArchitecture: amd64\nStatus: install ok installed\n" + (includeRemovedBaseline ? "\nPackage: curl\nVersion: 8.5.0\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: libcurl4t64\nVersion: 8.5.0\nArchitecture: amd64\nStatus: install ok installed\n" : string.Empty);
        var files = new Dictionary<string, byte[]>
        {
            ["opt/keycloak/bin/kc.sh"] = Encoding.UTF8.GetBytes("#!/bin/sh\n"),
            ["opt/keycloak/bin/kcadm.sh"] = Encoding.UTF8.GetBytes("#!/bin/sh\n"),
            ["opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar"] = JarBytes("keycloak-admin-cli", "26.7.2"),
            ["opt/keycloak/lib/server.jar"] = JarBytes("keycloak-server", "26.7.2"),
            ["opt/keycloak/keycloak.jar"] = JarBytes("keycloak-template-source", "26.7.2"),
            ["usr/lib/openssl.so"] = Encoding.UTF8.GetBytes("openssl"),
            ["usr/lib/openssl-provider-legacy.so"] = Encoding.UTF8.GetBytes("openssl-provider-legacy"),
            ["usr/lib/libssl3t64.so"] = Encoding.UTF8.GetBytes("libssl3t64"),
            ["var/lib/dpkg/status"] = Encoding.UTF8.GetBytes(status),
            ["etc/dpkg/dpkg.cfg.d/excludes"] = Encoding.UTF8.GetBytes("path-exclude=/usr/share/doc/*\n"),
            ["etc/passwd"] = Encoding.UTF8.GetBytes("root:x:0:0:root:/root:/bin/sh\nkeycloak:x:1000:0:Keycloak:/opt/keycloak:/usr/sbin/nologin\n"),
            ["etc/ssl/certs/ca-certificates.crt"] = Encoding.UTF8.GetBytes("fixture-certificate\n"),
            ["opt/java/openjdk/lib/security/cacerts"] = Encoding.UTF8.GetBytes("fixture-java-certificate\n"),
            ["opt"] = [],
            ["opt/keycloak"] = [],
            ["opt/keycloak/bin"] = [],
            ["opt/keycloak/bin/client"] = [],
            ["opt/keycloak/lib"] = [],
            ["usr"] = [],
            ["usr/lib"] = [],
            ["opt/java"] = [],
            ["opt/java/openjdk"] = [],
            ["opt/java/openjdk/lib"] = [],
            ["opt/java/openjdk/lib/security"] = [],
            ["etc"] = [],
            ["etc/dpkg"] = [],
            ["etc/dpkg/dpkg.cfg.d"] = [],
            ["etc/ssl"] = [],
            ["etc/ssl/certs"] = [],
            ["var"] = [],
            ["var/lib"] = [],
            ["var/lib/dpkg"] = []
        };
        var directories = files.Where(x => x.Value.Length == 0).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        if (extra is not null)
        {
            files["opt/keycloak/lib/server.jar"] = File.ReadAllBytes(extra.Paths[includeRemovedBaseline ? "maven-source" : "maven-replacement"]);
            files["opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar"] = File.ReadAllBytes(extra.Paths[includeRemovedBaseline ? "cli-source" : "cli-repaired"]);
            files["opt/keycloak/keycloak.jar"] = File.ReadAllBytes(extra.Paths["template-baseline"]);
            files["opt/keycloak/lib/jackson-core.jar"] = File.ReadAllBytes(extra.Paths["jackson-core-source"]);
            files["opt/keycloak/lib/jackson-databind.jar"] = File.ReadAllBytes(extra.Paths["jackson-databind-source"]);
            if (!includeRemovedBaseline)
            {
                files["opt/keycloak/bin/skoolit-entrypoint.sh"] = File.ReadAllBytes(extra.Paths["entrypoint"]);
                files["opt/keycloak/themes/keycloak.v2/login/login.ftl"] = File.ReadAllBytes(extra.Paths["theme-input"]);
                foreach (var path in new[] { "opt/keycloak/themes", "opt/keycloak/themes/keycloak.v2", "opt/keycloak/themes/keycloak.v2/login" }) { directories.Add(path); files[path] = []; }
            }
        }
        var layerTar = TarBytes(files, directories);
        var diffId = "sha256:" + Hex(layerTar); var layer = Gzip(layerTar); var layerDigest = "sha256:" + Hex(layer);
        var config = JsonBytes(writer => { writer.WriteStartObject(); writer.WriteString("architecture", "amd64"); writer.WriteString("os", "linux"); writer.WritePropertyName("config"); writer.WriteStartObject(); writer.WriteString("User", "1000:0"); writer.WritePropertyName("Entrypoint"); writer.WriteStartArray(); writer.WriteStringValue(extra is not null && !includeRemovedBaseline ? "/opt/keycloak/bin/skoolit-entrypoint.sh" : "/opt/keycloak/bin/kc.sh"); writer.WriteEndArray(); writer.WritePropertyName("Env"); writer.WriteStartArray(); writer.WriteStringValue("KC_RUN_IN_CONTAINER=true"); writer.WriteEndArray(); writer.WriteEndObject(); writer.WritePropertyName("rootfs"); writer.WriteStartObject(); writer.WriteString("type", "layers"); writer.WritePropertyName("diff_ids"); writer.WriteStartArray(); writer.WriteStringValue(diffId); writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); });
        var configDigest = "sha256:" + Hex(config); var manifest = JsonBytes(writer => { writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("mediaType", "application/vnd.oci.image.manifest.v1+json"); writer.WritePropertyName("config"); Descriptor(writer, "application/vnd.oci.image.config.v1+json", configDigest, config.Length); writer.WritePropertyName("layers"); writer.WriteStartArray(); Descriptor(writer, "application/vnd.oci.image.layer.v1.tar+gzip", layerDigest, layer.Length); writer.WriteEndArray(); writer.WriteEndObject(); });
        var manifestDigest = "sha256:" + Hex(manifest); var index = JsonBytes(writer => { writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("mediaType", "application/vnd.oci.image.index.v1+json"); writer.WritePropertyName("manifests"); writer.WriteStartArray(); writer.WriteStartObject(); writer.WriteString("mediaType", "application/vnd.oci.image.manifest.v1+json"); writer.WriteString("digest", manifestDigest); writer.WriteNumber("size", manifest.Length); writer.WritePropertyName("platform"); writer.WriteStartObject(); writer.WriteString("architecture", "amd64"); writer.WriteString("os", "linux"); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject(); });
        var archive = TarBytes(new Dictionary<string, byte[]> { ["oci-layout"] = Encoding.UTF8.GetBytes("{\"imageLayoutVersion\":\"1.0.0\"}"), ["index.json"] = index, ["blobs/sha256/" + manifestDigest[7..]] = manifest, ["blobs/sha256/" + configDigest[7..]] = config, ["blobs/sha256/" + layerDigest[7..]] = layer });
        return (index, archive, "sha256:" + Hex(index), manifestDigest, configDigest, diffId);
    }

    private static byte[] JarBytes(string artifact, string version)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var properties = zip.CreateEntry("META-INF/maven/org.keycloak/" + artifact + "/pom.properties");
            properties.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var writer = new StreamWriter(properties.Open()))
                writer.Write($"groupId=org.keycloak\nartifactId={artifact}\nversion={version}\n");

            var classes = zip.CreateEntry("org/keycloak/fixture/Marker.class");
            classes.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = classes.Open();
            stream.WriteByte(1);
        }
        return output.ToArray();
    }
    private static void Descriptor(Utf8JsonWriter writer, string mediaType, string digest, int size) { writer.WriteStartObject(); writer.WriteString("mediaType", mediaType); writer.WriteString("digest", digest); writer.WriteNumber("size", size); writer.WriteEndObject(); }
    private static byte[] JsonBytes(Action<Utf8JsonWriter> write) { using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output)) { write(writer); writer.Flush(); } return output.ToArray(); }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
