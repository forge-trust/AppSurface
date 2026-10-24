using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

// Independently authored source and expected member maps. No executor or inspector
// generates the expected repaired archive; this fixture also exposes every input byte.
internal sealed class CompleteRecipeFixture : IDisposable
{
    private CompleteRecipeFixture(string root, IReadOnlyDictionary<string, string> paths)
    {
        Root = root; Paths = paths;
        Hashes = paths.ToDictionary(x => x.Key, x => Sha256(File.ReadAllBytes(x.Value)), StringComparer.Ordinal);
        Lengths = paths.ToDictionary(x => x.Key, x => new FileInfo(x.Value).Length, StringComparer.Ordinal);
    }
    internal string Root { get; }
    internal IReadOnlyDictionary<string, string> Paths { get; }
    internal IReadOnlyDictionary<string, string> Hashes { get; }
    internal IReadOnlyDictionary<string, long> Lengths { get; }
    internal string SourceArtifactPath => Paths["maven-source"];
    internal string ReplacementArtifactPath => Paths["maven-replacement"];
    internal string SourceCliPath => Paths["cli-source"];
    internal string ReplacementCorePath => Paths["jackson-core-replacement"];
    internal string ReplacementDatabindPath => Paths["jackson-databind-replacement"];
    internal string TemplateBaselinePath => Paths["template-baseline"];
    internal string TemplateEntry => "theme/keycloak.v2/login/login.ftl";
    internal string TemplateEntrySha256 => Hashes["baseline-entry"];
    internal string TemplateArchivePath => "/opt/keycloak/keycloak.jar";
    internal string CliDestination => "/opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar";

    internal static CompleteRecipeFixture Create()
    {
        var root = Path.Combine(KeycloakSecurity.PrivateTemporaryRoot(), "keycloak-complete-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["maven-source"] = Zip(Maven("com.example", "security-adapter", "1.0.0", "com/example/Marker.class", "source-class")),
                ["maven-replacement"] = Zip(Maven("com.example", "security-adapter", "2.0.0", "com/example/Marker.class", "replacement-class")),
                ["cli-source"] = Zip(CliMembers("2.21.5", "source")),
                ["cli-repaired"] = Zip(CliMembers("2.21.7", "replacement")),
                ["jackson-core-source"] = Zip(ModuleMembers("jackson-core", "2.21.5", "core", "source")),
                ["jackson-databind-source"] = Zip(ModuleMembers("jackson-databind", "2.21.5", "databind", "source")),
                ["jackson-core-replacement"] = Zip(ModuleMembers("jackson-core", "2.21.7", "core", "replacement")),
                ["jackson-databind-replacement"] = Zip(ModuleMembers("jackson-databind", "2.21.7", "databind", "replacement")),
                ["template-baseline"] = Zip(new Dictionary<string, string> { ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0\r\n\r\n", ["theme/keycloak.v2/login/login.ftl"] = "<#-- reviewed fixture template -->\n" }),
                ["baseline-entry"] = Encoding.UTF8.GetBytes("<#-- reviewed fixture template -->\n"),
                ["theme-input"] = Encoding.UTF8.GetBytes("<#-- owned replacement theme -->\n"),
                ["entrypoint"] = Encoding.UTF8.GetBytes("#!/bin/sh\nexec /opt/keycloak/bin/kc.sh \"$@\"\n")
            };
            var paths = bytes.ToDictionary(x => x.Key, x => Path.Combine(root, x.Key), StringComparer.Ordinal);
            foreach (var item in bytes) File.WriteAllBytes(paths[item.Key], item.Value);
            return new(root, paths);
        }
        catch { Directory.Delete(root, true); throw; }
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    private static Dictionary<string, string> Maven(string group, string artifact, string version, string className, string marker) => new()
    {
        ["META-INF/MANIFEST.MF"] = "Manifest-Version: 1.0\r\n\r\n",
        [$"META-INF/maven/{group}/{artifact}/pom.properties"] = $"groupId={group}\nartifactId={artifact}\nversion={version}\n",
        [className] = marker
    };
    private static Dictionary<string, string> ModuleMembers(string artifact, string version, string part, string marker)
    {
        var name = part == "core" ? "JsonFactory" : "ObjectMapper";
        var values = Maven("com.fasterxml.jackson.core", artifact, version, $"com/fasterxml/jackson/{part}/{name}.class", marker + "-" + part);
        var service = part == "core" ? "com.fasterxml.jackson.core.JsonFactory" : "com.fasterxml.jackson.core.ObjectCodec";
        values["META-INF/services/" + service] = $"com.fasterxml.jackson.{part}.{name}\n";
        return values;
    }
    private static Dictionary<string, string> CliMembers(string version, string marker)
    {
        var values = Maven("org.keycloak", "keycloak-admin-cli", "26.7.2", "org/keycloak/fixture/Marker.class", "preserved-marker");
        foreach (var (artifact, part) in new[] { ("jackson-core", "core"), ("jackson-databind", "databind") })
            foreach (var item in ModuleMembers(artifact, version, part, marker))
                if (item.Key != "META-INF/MANIFEST.MF") values.Add(item.Key, item.Value);
        return values;
    }
    private static byte[] Zip(Dictionary<string, string> members)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            foreach (var item in members.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(item.Key, CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = unchecked((int)0x81a40000);
                using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(item.Value));
            }
        return output.ToArray();
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
