using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInRuntimeLocatorTests
{
    [Fact]
    public void ResolveReportGeneratorDllReturnsCompletePinnedAssemblyRelativePayload()
    {
        var locator = new StrictReportGeneratorRuntimeLocator();
        var payload = locator.ResolveRuntime(AppContext.BaseDirectory);

        Assert.Equal("5.5.10", payload.Version);
        Assert.Equal("net10.0", payload.TargetFramework);
        Assert.EndsWith(Path.Combine("appsurface-coverage-runtime", "reportgenerator", "5.5.10", "net10.0"), payload.RootDirectory, StringComparison.Ordinal);
        Assert.True(File.Exists(payload.EntryPointPath));
        Assert.True(File.Exists(payload.DependencyManifestPath));
        Assert.True(File.Exists(payload.RuntimeConfigurationPath));
        Assert.True(File.Exists(Path.Join(payload.RootDirectory, "appsettings.json")));
        Assert.True(File.Exists(Path.Join(payload.RootDirectory, "runtimes", "win", "lib", "net10.0", "System.Diagnostics.EventLog.dll")));
        Assert.Equal(payload.EntryPointPath, locator.ResolveReportGeneratorDll());
    }

    [Fact]
    public void MissingAssemblyRelativePayloadFailsWithoutUsingOtherRuntimeLocations()
    {
        using var temp = TemporaryDirectory.Create("coverage-fanin-rg-missing-");

        var exception = Assert.Throws<CoverageExecutionException>(
            () => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(temp.Path));

        Assert.Equal("ASCOV140", exception.Code);
        Assert.Contains("failed validation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedAssetHashIsRejected()
    {
        using var temp = CopyPayloadToTemporaryDirectory();
        var payloadRoot = GetRuntimeDirectory(temp.Path);
        var entryPoint = Path.Join(payloadRoot, "ReportGenerator.dll");
        File.AppendAllText(entryPoint, "changed");

        var exception = Assert.Throws<CoverageExecutionException>(
            () => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(temp.Path));

        Assert.Equal("ASCOV140", exception.Code);
        Assert.Contains("pinned hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrUnlistedAssetsAreRejected()
    {
        using var missing = CopyPayloadToTemporaryDirectory();
        File.Delete(Path.Join(GetRuntimeDirectory(missing.Path), "ReportGenerator.deps.json"));

        var missingException = Assert.Throws<CoverageExecutionException>(
            () => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(missing.Path));

        Assert.Equal("ASCOV140", missingException.Code);
        Assert.Contains("required runtime file is missing", missingException.Message, StringComparison.Ordinal);

        using var extra = CopyPayloadToTemporaryDirectory();
        File.WriteAllText(Path.Join(GetRuntimeDirectory(extra.Path), "unlisted.txt"), "unexpected");

        var extraException = Assert.Throws<CoverageExecutionException>(
            () => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(extra.Path));

        Assert.Equal("ASCOV140", extraException.Code);
        Assert.Contains("unlisted runtime file", extraException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedManifestParserRejectsInvalidIdentityInventoryAndPaths()
    {
        var pinned = File.ReadAllBytes(Path.Join(GetRuntimeDirectory(AppContext.BaseDirectory), "runtime-manifest.json"));
        var valid = StrictReportGeneratorRuntimeLocator.ParseManifest(pinned);
        Assert.Contains("ReportGenerator.dll", valid.Keys);
        foreach (var mutation in new Action<JsonNode>[]
        {
            n => n["schemaVersion"] = 2, n => n["component"] = "Other", n => n["version"] = "5.5.11",
            n => n["targetFramework"] = "net9.0", n => n["entryPoint"] = "Other.dll",
            n => n["extra"] = true, n => n.AsObject().Remove("version"),
            n => n["files"] = new JsonArray(), n => n["files"]![0] = "invalid",
            n => n["files"]![0]!["sha256"] = null, n => n["files"]![0]!["sha256"] = "short",
            n => n["files"]![0]!["sha256"] = new string('z', 64),
            n => n["files"]!.AsArray().Add(n["files"]![0]!.DeepClone()),
            n => n["files"]![0]!["path"] = "../outside.dll", n => n["files"]![0]!["path"] = "/outside.dll",
            n => n["files"]![0]!["path"] = "win\\file.dll", n => n["files"]![0]!["path"] = "a//file.dll",
            n => n["files"]![0]!["path"] = "a/./file.dll", n => n["files"]![0]!["path"] = "",
            n => n["files"]!.AsArray().Remove(n["files"]!.AsArray().Single(f => f!["path"]!.GetValue<string>() == "ReportGenerator.dll"))
        })
        {
            var node = JsonNode.Parse(pinned)!;
            mutation(node);
            Assert.Throws<InvalidDataException>(() => StrictReportGeneratorRuntimeLocator.ParseManifest(
                Encoding.UTF8.GetBytes(node.ToJsonString())));
        }
        Assert.Throws<InvalidDataException>(() => StrictReportGeneratorRuntimeLocator.ParseManifest(Encoding.UTF8.GetBytes("[]")));
        var duplicate = Encoding.UTF8.GetString(pinned).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => StrictReportGeneratorRuntimeLocator.ParseManifest(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void TruncatedPinnedManifestFailsBeforeAnyRuntimeExecution()
    {
        using var temp = CopyPayloadToTemporaryDirectory();
        var manifest = Path.Join(GetRuntimeDirectory(temp.Path), "runtime-manifest.json");
        File.WriteAllText(manifest, "{");
        var error = Assert.Throws<CoverageExecutionException>(() => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(temp.Path));
        Assert.Equal("ASCOV140", error.Code);
        Assert.Contains("unexpected size", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest-directory")]
    [InlineData("linked-file")]
    [InlineData("linked-directory")]
    [InlineData("linked-root")]
    public void RuntimePayloadRejectsNonregularEntriesAndPreservesOutsideData(string mutation)
    {
        using var temp = CopyPayloadToTemporaryDirectory();
        using var outside = TemporaryDirectory.Create("coverage-fanin-runtime-outside-");
        var sentinel = TestPathUtils.PathUnder(outside.Path, "sentinel.txt");
        File.WriteAllText(sentinel, "outside bytes");
        var runtime = GetRuntimeDirectory(temp.Path);
        switch (mutation)
        {
            case "manifest-directory":
                var manifest = Path.Join(runtime, "runtime-manifest.json");
                File.Delete(manifest); Directory.CreateDirectory(manifest);
                break;
            case "linked-file":
                TestFileSystem.CreateFileLinkOrSkip(Path.Join(runtime, "unlisted-link"), sentinel);
                break;
            case "linked-directory":
                TestFileSystem.CreateDirectoryLinkOrSkip(Path.Join(runtime, "unlisted-directory"), outside.Path);
                break;
            case "linked-root":
                var owned = runtime + "-retained";
                Directory.Move(runtime, owned);
                TestFileSystem.CreateDirectoryLinkOrSkip(runtime, owned);
                break;
        }
        var error = Assert.Throws<CoverageExecutionException>(() => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(temp.Path));
        Assert.Equal("ASCOV140", error.Code);
        Assert.Equal("outside bytes", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFiles(outside.Path));
    }

    [Fact]
    public void ChangedOrVersionSkewedRuntimeManifestIsRejected()
    {
        using var temp = CopyPayloadToTemporaryDirectory();
        var manifestPath = Path.Join(GetRuntimeDirectory(temp.Path), "runtime-manifest.json");
        var original = File.ReadAllText(manifestPath);
        File.WriteAllText(manifestPath, original.Replace("5.5.10", "5.5.11", StringComparison.Ordinal));

        var exception = Assert.Throws<CoverageExecutionException>(
            () => new StrictReportGeneratorRuntimeLocator().ResolveRuntime(temp.Path));

        Assert.Equal("ASCOV140", exception.Code);
        Assert.Contains("differs from the assembly's pinned manifest", exception.Message, StringComparison.Ordinal);
    }

    private static TemporaryDirectory CopyPayloadToTemporaryDirectory()
    {
        var temp = TemporaryDirectory.Create("coverage-fanin-rg-fixture-");
        var sourceRoot = GetRuntimeDirectory(AppContext.BaseDirectory);
        var destinationRoot = GetRuntimeDirectory(temp.Path);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(TestPathUtils.PathUnder(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destination = TestPathUtils.PathUnder(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return temp;
    }

    private static string GetRuntimeDirectory(string assemblyDirectory)
        => Path.Join(assemblyDirectory, "appsurface-coverage-runtime", "reportgenerator", "5.5.10", "net10.0");

    private sealed class TemporaryDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TemporaryDirectory Create(string prefix)
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
