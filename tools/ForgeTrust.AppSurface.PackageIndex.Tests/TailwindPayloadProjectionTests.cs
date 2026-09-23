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
    public void Verify_RejectsUnsupportedAssetsGroup()
    {
        using var fixture = new PackageFixture();
        using var target = ParseTarget("{ \"unknownAssetsGroup\": {} }");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("Unsupported NuGet assets group", error.Message, StringComparison.Ordinal);
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

    [Fact]
    public void Verify_RejectsRestoredProtectedSymlinkWhenSupported()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "targets");
        var outside = Path.Combine(fixture.Root, "outside.targets");
        File.WriteAllText(outside, "targets");
        File.CreateSymbolicLink(Path.Combine(fixture.RestoredDirectory, "build", "linked.targets"), outside);
        using var target = ParseTarget("{}");

        var error = Assert.Throws<PackageIndexException>(() => fixture.Verify(target.RootElement));

        Assert.Contains("link/reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_RejectsCaseCollidingExtraExtractedProtectedFile()
    {
        using var fixture = new PackageFixture();
        fixture.Add("build/Tailwind.targets", "expected");
        var buildDirectory = Path.Combine(fixture.RestoredDirectory, "build");
        var collidingPath = Path.Combine(buildDirectory, "TAILWIND.targets");
        File.WriteAllText(collidingPath, "extra");
        if (!File.Exists(collidingPath) || Directory.EnumerateFiles(buildDirectory).Count() < 2) return;
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
