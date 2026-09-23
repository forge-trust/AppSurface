using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindProducerSubjectTests : IDisposable
{
    private const string Version = "1.2.3-ci.798";
    private const string TailwindId = "ForgeTrust.AppSurface.Web.Tailwind";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tailwind-subject-tests", Guid.NewGuid().ToString("N"));
    private readonly string _artifacts;

    public TailwindProducerSubjectTests()
    {
        _artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(_artifacts);
    }

    [Fact]
    public async Task CreateAndValidate_BindsExactManifestArchiveAndProducerIdentity()
    {
        var fixture = await CreateFixtureAsync();
        var closure = new[] { fixture.Package };

        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "2", new string('A', 40), closure, CancellationToken.None);
        var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        var validated = await TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            subjectHash, "12345", "98765", new string('a', 40), "222", CancellationToken.None);

        Assert.Equal(TailwindProofSubjectService.SchemaName, validated.Schema);
        Assert.Equal(new string('a', 40), validated.SourceCommit);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.ManifestBytes)).ToLowerInvariant(), validated.ArtifactManifestSha256);
        Assert.Equal(closure, validated.FirstPartyPackages);
    }

    [Theory]
    [InlineData("0001", "1", "2", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "01", "2", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "01", "1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "1", "1", "not-a-commit")]
    public async Task Create_RejectsNonCanonicalProducerIdentity(string repository, string run, string attempt, string commit)
    {
        var fixture = await CreateFixtureAsync();
        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, repository, run, attempt, commit, [fixture.Package], CancellationToken.None));
    }

    [Theory]
    [InlineData("0001", "1", "1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "01", "1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "1", "00", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1", "1", "1", "not-a-commit")]
    public void ValidateProducerContext_RejectsNonCanonicalWorkflowIdentity(string repository, string run, string attempt, string commit)
    {
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateProducerContext(repository, run, attempt, commit));
    }

    [Theory]
    [InlineData("wrong-package")]
    [InlineData("wrong-framework")]
    [InlineData("unsupported-projection")]
    public void ValidateSubject_RejectsUnsupportedPackageContract(string mutation)
    {
        var subject = mutation switch
        {
            "wrong-package" => MinimalSubject() with { PackageId = "ForgeTrust.Other" },
            "wrong-framework" => MinimalSubject() with { ConsumerFramework = "net9.0" },
            _ => MinimalSubject() with { PayloadProjectionVersion = 2 }
        };

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(subject));

        Assert.Contains("unsupported Tailwind package, framework, or payload projection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RejectsChangedArchiveBytesEvenWhenSubjectAndManifestRemainUntouched()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var hash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        await File.AppendAllTextAsync(TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName), "changed");

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            hash, "12345", "98765", new string('a', 40), "222", CancellationToken.None));
    }

    [Fact]
    public async Task Validate_RejectsManifestSubstitution()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var hash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        await File.AppendAllTextAsync(fixture.ManifestPath, "\n");

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            hash, "12345", "98765", new string('a', 40), "222", CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.nupkg")]
    [InlineData("nested\\escape.nupkg")]
    [InlineData("C:escape.nupkg")]
    public void ValidateSubject_RejectsUnconfinedArtifactNames(string name)
    {
        var subject = MinimalSubject() with { ArtifactFileName = name };
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(subject));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("net10.0/win-x64")]
    [InlineData("wrong-target")]
    public async Task ReadResolvedClosure_RejectsEscapedOrNonExactTargetGraph(string targetName)
    {
        var fixture = await CreateFixtureAsync();
        var graph = Assets(targetName);
        var path = Path.Combine(_root, "project.assets.json");
        await File.WriteAllTextAsync(path, graph);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsMalformedJsonWithJsonException()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = Path.Combine(_root, "malformed.assets.json");
        await File.WriteAllTextAsync(path, "[");

        Assert.ThrowsAny<JsonException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsAssetsGraphOverDocumentLimitBeforeParsing()
    {
        var fixture = await CreateFixtureAsync();
        var path = TestPathUtils.PathUnder(_root, "oversized.assets.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(TailwindProofSubjectService.MaximumDocumentBytes + 1);

        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
        Assert.Contains("exceeds 16 MiB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResolvedClosure_AcceptsValidPackageGraphAndReturnsBoundProducerClosure()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = Path.Combine(_root, "valid.assets.json");
        await File.WriteAllTextAsync(path, Assets("net10.0"));

        var closure = TailwindProofSubjectService.ReadResolvedClosure(path, manifest);

        Assert.Equal([fixture.Package], closure);
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsMissingAndUnplannedFirstPartyGraphNodes()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var missing = Path.Combine(_root, "missing.assets.json");
        await File.WriteAllTextAsync(missing, Assets("net10.0", includeTailwind: false));
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(missing, manifest));
        var extra = Path.Combine(_root, "extra.assets.json");
        await File.WriteAllTextAsync(extra, Assets("net10.0", extraPackage: true));
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(extra, manifest));
    }

    [Theory]
    [InlineData("wrong-version")]
    [InlineData("project-reference")]
    [InlineData("missing-library")]
    public async Task ReadResolvedClosure_RejectsInvalidFirstPartyPackageMetadata(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = Path.Combine(_root, "mutated.assets.json");
        await File.WriteAllTextAsync(path, Assets("net10.0", mutation: mutation));

        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
    }

    [Theory]
    [InlineData("multiple-frameworks")]
    [InlineData("runtime-identifier")]
    [InlineData("malformed-dependencies")]
    [InlineData("missing-first-party-dependency")]
    [InlineData("wrong-dependency-version")]
    public async Task ReadResolvedClosure_RejectsUntrustedFrameworkAndDependencyMetadata(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = Path.Combine(_root, "invalid-closure.assets.json");
        await File.WriteAllTextAsync(path, Assets("net10.0", mutation: mutation));

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));

        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public async Task ReadResolvedClosure_AllowsThirdPartyDependenciesOutsideProducerClosure()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = Path.Combine(_root, "third-party-dependency.assets.json");
        await File.WriteAllTextAsync(path, Assets("net10.0", mutation: "third-party-dependency"));

        Assert.Equal([fixture.Package], TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("C:/drive")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("CON/file")]
    [InlineData("name./file")]
    public void NormalizeArchivePath_RejectsUnsafeRawComponents(string raw)
    {
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.NormalizeArchivePath(raw));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("assets\\escape.txt")]
    [InlineData("build/../escape.txt")]
    public async Task Create_RejectsUnsafePathsInsideRealPackageArchive(string unsafeEntry)
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            Add(archive, $"{TailwindId}.nuspec", $"<package><metadata><id>{TailwindId}</id><version>{Version}</version></metadata></package>");
            Add(archive, "build/tailwind.release.json", TailwindReleaseManifest());
            Add(archive, unsafeEntry, "mutated path payload");
        }

        var changedPackageHash = PackageHash.ComputeSha512(packagePath);
        var changedManifest = JsonSerializer.SerializeToUtf8Bytes(new PackageArtifactManifest(
            1, Version, DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            [new PackageArtifactManifestEntry(TailwindId, "Web/Tailwind.csproj", "publish", fixture.Package.ArtifactFileName, changedPackageHash, false)]), PackageArtifactJson.Options);
        await File.WriteAllBytesAsync(fixture.ManifestPath, changedManifest);

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsCorruptTailwindArchive()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3, 4]);
        var updatedEntry = new PackageArtifactManifestEntry(TailwindId, "Web/Tailwind.csproj", "publish", fixture.Package.ArtifactFileName, PackageHash.ComputeSha512(packagePath), false);
        var updatedManifest = JsonSerializer.SerializeToUtf8Bytes(new PackageArtifactManifest(
            1, Version, DateTimeOffset.Parse("2026-09-23T00:00:00Z"), [updatedEntry]), PackageArtifactJson.Options);
        await File.WriteAllBytesAsync(fixture.ManifestPath, updatedManifest);

        await Assert.ThrowsAsync<InvalidDataException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None));
    }

    private async Task<(string ManifestPath, byte[] ManifestBytes, TailwindSubjectPackage Package)> CreateFixtureAsync()
    {
        var fileName = $"{TailwindId}.{Version}.nupkg";
        var path = TestPathUtils.PathUnder(_artifacts, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, $"{TailwindId}.nuspec", $"<package><metadata><id>{TailwindId}</id><version>{Version}</version></metadata></package>");
            Add(archive, "build/tailwind.release.json", TailwindReleaseManifest());
            Add(archive, "build/tailwind.version", "4.1.0");
            Add(archive, "contentFiles/any/any/tailwind.css", ".fixture{color:red}");
        }

        var package = new TailwindSubjectPackage(TailwindId, Version, fileName, PackageHash.ComputeSha512(path));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PackageArtifactManifest(
            1, Version, DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            [new PackageArtifactManifestEntry(TailwindId, "Web/Tailwind.csproj", "publish", fileName, package.PackageSha512, false)]), PackageArtifactJson.Options);
        var manifestPath = Path.Combine(_artifacts, "package-artifacts.json");
        await File.WriteAllBytesAsync(manifestPath, bytes);
        return (manifestPath, bytes, package);
    }

    private static string Assets(string targetName, bool includeTailwind = true, bool extraPackage = false, string? mutation = null)
    {
        var target = new Dictionary<string, object>();
        var libraries = new Dictionary<string, object>();
        if (includeTailwind)
        {
            var resolvedVersion = mutation == "wrong-version" ? "9.9.9" : Version;
            var key = $"{TailwindId}/{resolvedVersion}";
            var dependencies = mutation switch
            {
                "malformed-dependencies" => (object)new[] { "invalid" },
                "missing-first-party-dependency" => new Dictionary<string, string> { ["ForgeTrust.Unplanned"] = Version },
                "wrong-dependency-version" => new Dictionary<string, string> { [TailwindId] = "9.9.9" },
                "third-party-dependency" => new Dictionary<string, string> { ["Newtonsoft.Json"] = "13.0.1" },
                _ => new Dictionary<string, string>()
            };
            target[key] = mutation == "project-reference"
                ? new { type = "project" }
                : new Dictionary<string, object> { ["type"] = "package", ["dependencies"] = dependencies };
            if (mutation != "missing-library")
                libraries[key] = new { type = mutation == "project-reference" ? "project" : "package", path = $"forgetrust.appsurface.web.tailwind/{resolvedVersion}", sha512 = "fixture-content-hash" };
        }
        if (extraPackage)
        {
            target[$"ForgeTrust.Unplanned/{Version}"] = new { type = "package" };
            libraries[$"ForgeTrust.Unplanned/{Version}"] = new { type = "package", path = $"forgetrust.unplanned/{Version}", sha512 = "fixture-content-hash" };
        }
        var targets = new Dictionary<string, object> { [targetName] = target };
        var assets = JsonSerializer.Serialize(new
        {
            targets,
            libraries,
            project = new
            {
                frameworks = mutation == "multiple-frameworks"
                    ? new Dictionary<string, object> { ["net10_0"] = new { }, ["net9_0"] = new { } }
                    : new Dictionary<string, object> { ["net10_0"] = new { } },
                restore = mutation == "runtime-identifier"
                    ? new Dictionary<string, object> { ["runtimeIdentifier"] = "linux-x64" }
                    : new Dictionary<string, object>()
            }
        }).Replace("net10_0", "net10.0", StringComparison.Ordinal);
        return assets.Replace("net9_0", "net9.0", StringComparison.Ordinal);
    }

    private static string TailwindReleaseManifest() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version = "4.1.0",
        baseUrl = "https://example.test/tailwind",
        assets = new[]
        {
            new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = new string('a', 64) },
            new { rid = "linux-arm64", binaryName = "tailwindcss-linux-arm64", sha256 = new string('b', 64) },
            new { rid = "osx-x64", binaryName = "tailwindcss-macos-x64", sha256 = new string('c', 64) },
            new { rid = "osx-arm64", binaryName = "tailwindcss-macos-arm64", sha256 = new string('d', 64) },
            new { rid = "win-x64", binaryName = "tailwindcss-windows-x64.exe", sha256 = new string('e', 64) }
        }
    });

    private static TailwindProofSubject MinimalSubject() => new(
        TailwindProofSubjectService.SchemaName, "1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "2", "1", Version,
        new string('a', 64), TailwindId, $"{TailwindId}.{Version}.nupkg", new string('b', 128), new string('c', 64), "net10.0",
        [new TailwindSubjectPackage(TailwindId, Version, $"{TailwindId}.{Version}.nupkg", new string('b', 128))]);

    private static void Add(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
