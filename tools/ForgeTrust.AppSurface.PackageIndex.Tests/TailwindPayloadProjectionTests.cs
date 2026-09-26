using System.IO.Compression;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindPayloadProjectionTests
{
    [Fact]
    public void Verify_ReturnsHashesForAssetsGraphAndEveryProtectedPackageFile()
    {
        using var fixture = new PackageFixture();
        fixture.Add("lib/net10.0/Tailwind.dll", "assembly");
        fixture.Add("build/Tailwind.targets", "targets");
        fixture.Add("buildTransitive/Tailwind.props", "transitive");
        fixture.Add("runtimes/linux-x64/native/tailwind", "native");
        fixture.Add("README.md", "not projected");

        using var target = JsonDocument.Parse("""
            {
              "type": "Package",
              "framework": ".NETCoreApp,Version=v10.0",
              "dependencies": { "Microsoft.NETCore.App.Ref": "10.0.0" },
              "frameworkAssemblies": [],
              "frameworkReferences": [ "Microsoft.NETCore.App" ],
              "compileOnly": false,
              "compile": { "lib/net10.0/Tailwind.dll": {} },
              "build": { "build/Tailwind.targets": {} },
              "runtimeTargets": {
                "runtimes/linux-x64/native/tailwind": {
                  "assetType": "native",
                  "rid": "linux-x64"
                }
              }
            }
            """);

        var hashes = TailwindPayloadProjection.Verify(fixture.ArchivePath, fixture.RestoredDirectory,
            target.RootElement, CancellationToken.None);

        Assert.Equal(4, hashes.Count);
        Assert.Contains("lib/net10.0/Tailwind.dll", hashes.Keys);
        Assert.Contains("build/Tailwind.targets", hashes.Keys);
        Assert.Contains("buildTransitive/Tailwind.props", hashes.Keys);
        Assert.Contains("runtimes/linux-x64/native/tailwind", hashes.Keys);
        Assert.DoesNotContain("README.md", hashes.Keys);
    }

    [Fact]
    public void Verify_AcceptsBooleanCompileOnlyMetadata()
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget("{ \"compileOnly\": true, \"dependencies\": { \"Other\": \"1.0.0\" } }");

        Assert.Empty(fixture.Verify(target.RootElement));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("lib/../outside.txt")]
    public void Verify_RejectsUnsafeAssetsGraphPaths(string path)
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget($"{{\"compile\":{{{JsonSerializer.Serialize(path)}:{{}}}}}}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsCaseCollidingAssetsGraphPaths()
    {
        using var fixture = new PackageFixture();
        fixture.Add("lib/net10.0/Tailwind.dll", "assembly");
        using var target = ParseTarget("""
            { "compile": { "lib/net10.0/Tailwind.dll": {}, "LIB/net10.0/tailwind.dll": {} } }
            """);

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("case-colliding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsMismatchedProtectedExtractedBytes()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "expected", "changed");
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("bytes changed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsMissingProtectedExtractedBytes()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "expected", omitRestored: true);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("count differs", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsArchiveFileMissingFromEqualSizedRestoredProjection()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/expected.targets", "expected", omitRestored: true);
        fixture.Add("build/other.targets", "other");
        File.WriteAllText(Path.Combine(fixture.RestoredDirectory, "build", "unexpected.targets"), "replacement");
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("missing 'build/expected.targets'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsArchiveFileUsedAsDirectoryAncestor()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build", "not a directory");
        fixture.Add("build/child.targets", "child", omitRestored: true);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("ZIP file/directory collision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsUnsupportedAssetsGroup()
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget("{ \"unknownAssetsGroup\": {} }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("Unsupported NuGet assets group", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsNonObjectFirstPartyTargetNode()
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget("[]");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("target node must be an object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsSelectedAssetMissingFromRestoredPackage()
    {
        using var fixture = new PackageFixture();
        fixture.Add("native/codec.bin", "archive bytes", omitRestored: true);
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin\": {} } }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("missing assets-graph path 'native/codec.bin'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsSelectedAssetBelowAFileInTheRestoredPackage()
    {
        using var fixture = new PackageFixture();
        fixture.Add("native/codec.bin/child", "archive bytes", omitRestored: true);
        var blockingFile = TestPathUtils.PathUnder(fixture.RestoredDirectory, "native", "codec.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(blockingFile)!);
        File.WriteAllText(blockingFile, "restored file occupies an ancestor");
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin/child\": {} } }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("missing assets-graph path", error.Message, StringComparison.Ordinal);
    }

    [CaseSensitiveFileSystemFact]
    public void Verify_RejectsAmbiguousCaseInsensitiveRestoredAssetLookup()
    {
        using var fixture = new PackageFixture();
        fixture.Add("native/codec.bin", "archive bytes");
        var alternate = TestPathUtils.PathUnder(fixture.RestoredDirectory, "native", "CODEC.bin");
        File.WriteAllText(alternate, "case-colliding restored bytes");
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin\": {} } }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("ambiguous case-insensitive path component", error.Message, StringComparison.Ordinal);
    }

    [UnixFileSystemFact]
    public void Verify_RejectsSelectedAssetLinkOutsideProtectedRoots()
    {
        using var fixture = new PackageFixture();
        fixture.Add("native/codec.bin", "archive bytes");
        var restored = TestPathUtils.PathUnder(fixture.RestoredDirectory, "native", "codec.bin");
        var outside = TestPathUtils.PathUnder(fixture.Root, "outside.bin");
        File.WriteAllText(outside, "archive bytes");
        File.Delete(restored);
        File.CreateSymbolicLink(restored, outside);
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin\": {} } }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("link/reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsMissingRestoredPackageDirectory()
    {
        using var fixture = new PackageFixture();
        Directory.Delete(fixture.RestoredDirectory, recursive: true);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("Restored package directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsSymlinkEntriesInPackageArchive()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/link.targets", "target", archiveMode: 0xA000);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("symlink", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsMalformedRuntimeTargetMetadata()
    {
        using var fixture = new PackageFixture();
        fixture.Add("runtimes/linux-x64/native/tailwind", "native");
        using var target = ParseTarget("""
            { "runtimeTargets": { "runtimes/linux-x64/native/tailwind": { "assetType": "content", "rid": "linux-x64" } } }
            """);

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("Malformed runtimeTargets metadata", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"type\": 1 }", "metadata")]
    [InlineData("{ \"framework\": [] }", "metadata")]
    [InlineData("{ \"dependencies\": { \"Other\": 1 } }", "dependency metadata")]
    [InlineData("{ \"dependencies\": [] }", "dependency metadata")]
    [InlineData("{ \"frameworkAssemblies\": {} }", "metadata")]
    [InlineData("{ \"frameworkReferences\": [1] }", "metadata")]
    [InlineData("{ \"compileOnly\": \"true\" }", "metadata")]
    [InlineData("{ \"runtime\": [] }", "path-keyed")]
    [InlineData("{ \"compile\": { \"native/codec.bin\": null } }", "path metadata")]
    [InlineData("{ \"runtimeTargets\": { \"native/codec.bin\": {} } }", "runtimeTargets metadata")]
    public void Verify_RejectsMalformedAssetMetadata(string json, string diagnostic)
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget(json);

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ProjectsSelectedNativeAssetOutsideProtectedRoots()
    {
        using var fixture = new PackageFixture();
        fixture.Add("native/codec.bin", "native payload");
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin\": {} } }");

        var projected = fixture.Verify(target.RootElement);

        Assert.Equal("native/codec.bin", Assert.Single(projected.Keys));
    }

    [Fact]
    public void Verify_RejectsSelectedNativeAssetMissingFromArchive()
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget("{ \"native\": { \"native/codec.bin\": {} } }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("missing archive entry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsDuplicateArchivePathEvenOutsideProjection()
    {
        using var fixture = new PackageFixture();
        fixture.Add("notes/readme.txt", "first");
        fixture.Add("notes/readme.txt", "second");
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("Duplicate", error.Message, StringComparison.Ordinal);
    }

    [UnixFileSystemFact]
    public void Verify_RejectsRestoredProtectedSymlinkWhenSupported()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "targets");
        var outside = Path.Combine(fixture.Root, "outside.targets");
        File.WriteAllText(outside, "targets");
        File.CreateSymbolicLink(Path.Combine(fixture.RestoredDirectory, "build", "linked.targets"), outside);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("link/reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [CaseSensitiveFileSystemFact]
    public void Verify_RejectsCaseCollidingExtraExtractedProtectedFile()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "expected");
        var buildDirectory = Path.Combine(fixture.RestoredDirectory, "build");
        var collidingPath = Path.Combine(buildDirectory, "TAILWIND.targets");
        File.WriteAllText(collidingPath, "extra");
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("case-colliding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [UnixFileSystemFact]
    public void Verify_RejectsRestoredPathsThatNormalizeToTheSameProjectionPath()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/A/B.targets", "expected");
        File.WriteAllText(Path.Combine(fixture.RestoredDirectory, "build", "A\\B.targets"), "extra");
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("case-colliding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument ParseTarget(string json) => JsonDocument.Parse(json);

    private sealed class PackageFixture : IDisposable
    {
        private readonly List<(string Path, string Contents, int? ArchiveMode)> _archiveFiles = [];

        internal PackageFixture()
        {
            Root = TestPathUtils.PathUnder(Path.GetTempPath(), "tailwind-payload-projection-tests", Guid.NewGuid().ToString("N"));
            RestoredDirectory = TestPathUtils.PathUnder(Root, "restored");
            ArchivePath = TestPathUtils.PathUnder(Root, "Tailwind.1.0.0.nupkg");
            Directory.CreateDirectory(RestoredDirectory);
            using var archive = ZipFile.Open(ArchivePath, ZipArchiveMode.Create);
        }

        internal string Root { get; }
        internal string RestoredDirectory { get; }
        internal string ArchivePath { get; }

        internal void Add(string path, string archiveContents, string? restoredContents = null, int? archiveMode = null,
            bool omitRestored = false)
        {
            using (var archive = ZipFile.Open(ArchivePath, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(path);
                if (archiveMode is not null) entry.ExternalAttributes = archiveMode.Value << 16;
                using var writer = new StreamWriter(entry.Open());
                writer.Write(archiveContents);
            }
            if (omitRestored) return;

            var restoredPath = TestPathUtils.PathUnder(RestoredDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
            File.WriteAllText(restoredPath, restoredContents ?? archiveContents);
        }

        internal IReadOnlyDictionary<string, string> Verify(JsonElement target) =>
            TailwindPayloadProjection.Verify(ArchivePath, RestoredDirectory, target, CancellationToken.None);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Discovers case-collision assertions only when the test volume preserves distinct casing.</summary>
public sealed class CaseSensitiveFileSystemFactAttribute : FactAttribute
{
    public CaseSensitiveFileSystemFactAttribute()
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "tailwind-case-probe", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(TestPathUtils.PathUnder(root, "asset"), "lowercase");
            File.WriteAllText(TestPathUtils.PathUnder(root, "ASSET"), "uppercase");
            if (Directory.EnumerateFiles(root).Count() != 2)
                Skip = "Requires a case-sensitive test filesystem.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Skip = $"Cannot probe test filesystem casing: {exception.Message}";
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>Discovers Unix-only path and symlink fixtures as explicit skips on Windows.</summary>
public sealed class UnixFileSystemFactAttribute : FactAttribute
{
    public UnixFileSystemFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires Unix filesystem path and symlink semantics.";
    }
}
