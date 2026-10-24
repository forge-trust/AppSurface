using System.IO.Compression;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// These tests exercise the bounded Maven identity seam with transient ZIP bytes.
// They do not represent approved coordinates or production artifacts.
public sealed class CaptureVerificationTests
{
    [Fact]
    public void VerifyMavenIdentity_accepts_exact_pom_properties()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties",
            "groupId=example\nartifactId=library\nversion=1.2.3\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path, Coordinate("example", "library", "1.2.3"));
    }

    [Fact]
    public void VerifyMavenIdentity_accepts_mssql_jre_coordinate_mapping()
    {
        using var jar = Jar(("META-INF/maven/com.microsoft.sqlserver/mssql-jdbc/pom.properties",
            "groupId=com.microsoft.sqlserver\nartifactId=mssql-jdbc\nversion=13.4.0\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("com.microsoft.sqlserver", "mssql-jdbc", "13.4.0.jre11"));
    }

    [Fact]
    public void VerifyMavenIdentity_accepts_bouncycastle_bundle_version()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nBundle-Version: 1.85\n\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("org.bouncycastle", "bcprov-jdk18on", "1.85"));
    }

    [Fact]
    public void VerifyMavenIdentity_accepts_manifest_implementation_version()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.0.1\n\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path, Coordinate("example", "manifest-only", "4.0.1"));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_version_mismatch_including_jre_mapping()
    {
        using var jar = Jar(("META-INF/maven/com.microsoft.sqlserver/mssql-jdbc/pom.properties",
            "groupId=com.microsoft.sqlserver\nartifactId=mssql-jdbc\nversion=13.4.0.jre11\n"));
        AssertMavenFailure("identity differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("com.microsoft.sqlserver", "mssql-jdbc", "13.4.0.jre11")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_duplicate_and_malformed_pom_properties()
    {
        using var duplicate = Jar(("META-INF/maven/example/library/pom.properties",
            "groupId=example\nartifactId=library\nversion=1.2.3\nversion=1.2.3\n"));
        AssertMavenFailure("Ambiguous Maven metadata", () => KeycloakSecurity.VerifyMavenIdentity(duplicate.Path,
            Coordinate("example", "library", "1.2.3")));

        using var malformed = Jar(("META-INF/maven/example/library/pom.properties",
            "groupId=example\nnot-a-property\nartifactId=library\nversion=1.2.3\n"));
        AssertMavenFailure("Ambiguous Maven metadata", () => KeycloakSecurity.VerifyMavenIdentity(malformed.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_duplicate_pom_entries()
    {
        using var jar = Jar(
            ("META-INF/maven/example/library/pom.properties", "groupId=example\nartifactId=library\nversion=1.2.3\n"),
            ("META-INF/maven/example/library/pom.properties", "groupId=example\nartifactId=library\nversion=1.2.3\n"));
        AssertMavenFailure("Duplicate/aliased entry", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_manifest_version_mismatch_and_missing_identity()
    {
        using var mismatch = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.0.0\n\n"));
        AssertMavenFailure("manifest version differs", () => KeycloakSecurity.VerifyMavenIdentity(mismatch.Path,
            Coordinate("example", "manifest-only", "4.0.1")));

        using var missing = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n\n"));
        AssertMavenFailure("bundle version differs", () => KeycloakSecurity.VerifyMavenIdentity(missing.Path,
            Coordinate("org.bouncycastle", "bcprov-jdk18on", "1.85")));
    }

    [Fact]
    public void VerifyMavenIdentity_accepts_manifest_continuation_in_main_section()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.\n 0.1\n\nName: ignored\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path, Coordinate("example", "manifest-only", "4.0.1"));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_manifest_continuation_without_previous_field()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "  orphan continuation\nManifest-Version: 1.0\n\n"));
        AssertMavenFailure("manifest continuation is ambiguous", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "manifest-only", "1.0")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_empty_manifest_continuation()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.\n \n\n"));
        AssertMavenFailure("manifest continuation is ambiguous", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "manifest-only", "4.0.1")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_duplicate_manifest_main_field()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 4.0.1\nImplementation-Version: 4.0.1\n\n"));
        AssertMavenFailure("ambiguous manifest main metadata", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "manifest-only", "4.0.1")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_manifest_field_without_separator()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version 4.0.1\n\n"));
        AssertMavenFailure("malformed manifest main metadata", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "manifest-only", "4.0.1")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_manifest_empty_field_name()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", ": value\n\n"));
        AssertMavenFailure("malformed manifest main metadata", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "manifest-only", "value")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_bouncycastle_bundle_version_mismatch()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nBundle-Version: 1.84\n\n"));
        AssertMavenFailure("bundle version differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("org.bouncycastle", "bcprov-jdk18on", "1.85")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_bouncycastle_bundle_continuation_mismatch()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nBundle-Version: 1.\n 84\n\n"));
        AssertMavenFailure("bundle version differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("org.bouncycastle", "bcprov-jdk18on", "1.85")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_missing_group_id()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties", "artifactId=library\nversion=1.2.3\n"));
        AssertMavenFailure("identity differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_missing_artifact_id()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties", "groupId=example\nversion=1.2.3\n"));
        AssertMavenFailure("identity differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_missing_version()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties", "groupId=example\nartifactId=library\n"));
        AssertMavenFailure("identity differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_mismatched_group_and_artifact()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties", "groupId=other\nartifactId=wrong\nversion=1.2.3\n"));
        AssertMavenFailure("identity differs", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void VerifyMavenIdentity_rejects_pom_comment_without_key_value()
    {
        using var jar = Jar(("META-INF/maven/example/library/pom.properties", "# comment\nnot-a-pair\ngroupId=example\nartifactId=library\nversion=1.2.3\n"));
        AssertMavenFailure("ambiguous Maven metadata", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path,
            Coordinate("example", "library", "1.2.3")));
    }

    [Fact]
    public void ManifestOnlyArtifactRequiresARecognizedVersionIdentity()
    {
        using var jar = Jar(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\n\r\n"));
        AssertMavenFailure("lacks a recognized version", () => KeycloakSecurity.VerifyMavenIdentity(jar.Path, Coordinate("example", "library", "1")));
    }

    [Theory]
    [InlineData("13.2.1.jre11", "13.2.1", null, "jar")]
    [InlineData("13.4.0.jre11", "13.4.0.jre11", "classifier", "jar")]
    [InlineData("13.4.0.jre11", "13.4.0.jre11", null, "zip")]
    public void OnlyReviewedJdbcCoordinateGetsItsClosedMetadataVersionMapping(string version, string metadataVersion, string? classifier, string extension)
    {
        using var jar = Jar(("META-INF/maven/com.microsoft.sqlserver/mssql-jdbc/pom.properties", $"groupId=com.microsoft.sqlserver\nartifactId=mssql-jdbc\nversion={metadataVersion}\n"));
        KeycloakSecurity.VerifyMavenIdentity(jar.Path, new("com.microsoft.sqlserver", "mssql-jdbc", version, classifier, extension));
    }

    [Theory]
    [InlineData("com.example:lib:1", null, "jar")]
    [InlineData("com.example:lib:1:tests:zip", "tests", "zip")]
    public void ExactModuleCoordinatesRetainClassifierAndExtension(string value, string? classifier, string extension)
    {
        var coordinate = KeycloakSecurity.ParseCoordinate(value);
        Assert.Equal(classifier, coordinate.Classifier); Assert.Equal(extension, coordinate.Extension);
        Assert.Throws<InvalidDataException>(() => KeycloakSecurity.ParseCoordinate("too:short"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("slash/id")]
    public void MaterialLocatorRejectsUnsafeIdentityBeforeOpening(string id)
        => Assert.Throws<InvalidDataException>(() => KeycloakSecurity.Material("owned", id));

    private static ArtifactCoordinate Coordinate(string group, string artifact, string version) =>
        new(group, artifact, version, null, "jar");

    private static void AssertMavenFailure(string fragment, Action action)
    {
        var exception = Assert.Throws<InvalidDataException>(action);
        Assert.Contains(fragment, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TemporaryJar Jar(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "appsurface-capture-verification-" + Guid.NewGuid().ToString("N") + ".jar");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return new(path);
    }

    private sealed class TemporaryJar(string path) : IDisposable
    {
        internal string Path { get; } = path;
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
