using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    [Fact]
    public async Task Create_RejectsTailwindArchiveChangedAfterManifestValidation()
    {
        var fixture = await CreateFixtureAsync();
        await File.AppendAllTextAsync(TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName), "changed after manifest validation");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None));

        Assert.Contains("changed after artifact-manifest validation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_DoesNotReplaceAnExistingSubjectAndCleansTemporaryFile()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var originalHash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => TailwindProofSubjectService.WriteAsync(subject with { ProducerAttempt = "2" }, _artifacts, CancellationToken.None));

        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName)))).ToLowerInvariant());
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "tailwind-proof-subject.json.*.tmp"));
    }

    [Fact]
    public async Task Write_CancellationRemovesIncompleteTemporarySubject()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailwindProofSubjectService.WriteAsync(subject, _artifacts, cancellation.Token));

        Assert.False(File.Exists(TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName)));
        Assert.Empty(Directory.EnumerateFiles(_artifacts, "tailwind-proof-subject.json.*.tmp"));
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
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("commit")]
    public void ValidateProducerContext_RejectsEmptyOrOutOfRangeIdentityValues(string field)
    {
        var repository = field == "repository" ? string.Empty : "1";
        var run = field == "run" ? new string('1', 21) : "2";
        var attempt = field == "attempt" ? string.Empty : "1";
        var commit = field == "commit" ? new string('g', 40) : new string('a', 40);

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
    public void ValidateSubject_RejectsUnsupportedSchema()
    {
        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(
            MinimalSubject() with { Schema = "appsurface-tailwind-proof-subject-v0" }));

        Assert.Contains("Unsupported Tailwind producer subject schema", error.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Validate_RejectsSubjectPathWithNonCanonicalFilename()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var hash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        var renamed = TestPathUtils.PathUnder(_artifacts, "renamed-subject.json");
        File.Copy(TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), renamed);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            renamed, _artifacts, fixture.ManifestPath, hash, "12345", "98765", new string('a', 40), "222", CancellationToken.None));

        Assert.Contains("must be named", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("manifest")]
    public async Task Validate_RejectsEvidenceFilesOutsideProducerDirectory(string evidenceFile)
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var hash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        var subjectPath = evidenceFile == "subject"
            ? TestPathUtils.PathUnder(_root, TailwindProofSubjectService.FileName)
            : TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName);
        var manifestPath = evidenceFile == "manifest"
            ? TestPathUtils.PathUnder(_root, "outside-manifest.json")
            : fixture.ManifestPath;

        await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            subjectPath, _artifacts, manifestPath, hash, "12345", "98765", new string('a', 40), "222", CancellationToken.None));
    }

    [Fact]
    public async Task Validate_RejectsSubjectDigestThatDiffersFromTrustedOutput()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            new string('0', 64), "12345", "98765", new string('a', 40), "222", CancellationToken.None));

        Assert.Contains("does not match the trusted workflow output", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("source")]
    public async Task Validate_RejectsProducerIdentityDifferentFromTrustedWorkflow(string identity)
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var hash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            hash, identity == "repository" ? "54321" : "12345", identity == "run" ? "98766" : "98765",
            identity == "source" ? new string('b', 40) : new string('a', 40), "222", CancellationToken.None));

        Assert.Contains("differs from trusted workflow identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RejectsManifestVersionDriftAfterRebindingManifestDigest()
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var changedManifest = Encoding.UTF8.GetString(fixture.ManifestBytes).Replace(Version, "1.2.4-ci.798", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.ManifestPath, changedManifest);
        subject = subject with { ArtifactManifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changedManifest))).ToLowerInvariant() };
        await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        var subjectHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName)))).ToLowerInvariant();

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            subjectHash,
            "12345", "98765", new string('a', 40), "222", CancellationToken.None));

        Assert.Contains("package version does not match", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("archive-name")]
    [InlineData("tailwind-manifest-hash")]
    [InlineData("first-party-hash")]
    public async Task Validate_RejectsSubjectPackageOrReleaseManifestSubstitution(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        subject = mutation switch
        {
            "archive-name" => subject with { ArtifactFileName = "other-valid-name.nupkg" },
            "tailwind-manifest-hash" => subject with { TailwindManifestSha256 = new string('d', 64) },
            _ => subject with { FirstPartyPackages = [fixture.Package with { PackageSha512 = new string('d', 128) }] }
        };
        await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);
        var subjectPath = TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(subjectPath))).ToLowerInvariant();

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            subjectPath, _artifacts, fixture.ManifestPath, hash, "12345", "98765", new string('a', 40), "222", CancellationToken.None));

        Assert.Contains(mutation switch
        {
            "archive-name" => "identity does not match",
            "tailwind-manifest-hash" => "release manifest bytes do not match",
            _ => "SHA-512 does not match the producer subject"
        }, error.Message, StringComparison.Ordinal);
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
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("file:name.nupkg")]
    public void ValidateSubject_RejectsInvalidBasenames(string name)
    {
        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(
            MinimalSubject() with { ArtifactFileName = name }));

        Assert.Contains("confined filename basename", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSubject_RejectsDuplicateCaseInsensitivePackagesAndUnsortedClosure()
    {
        var package = MinimalSubject().FirstPartyPackages.Single();
        var duplicate = MinimalSubject() with
        {
            FirstPartyPackages = [package, package with { PackageId = package.PackageId.ToLowerInvariant() }]
        };
        var duplicateError = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(duplicate));
        Assert.Contains("Duplicate first-party package", duplicateError.Message, StringComparison.Ordinal);

        var reversed = MinimalSubject() with
        {
            FirstPartyPackages = [
                new TailwindSubjectPackage("ForgeTrust.Zzz", Version, "z.nupkg", new string('e', 128)),
                package]
        };
        var orderError = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(reversed));
        Assert.Contains("ordinal-sorted", orderError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "closure is empty")]
    [InlineData("empty", "closure is empty")]
    [InlineData("missing-tailwind", "does not include Tailwind")]
    [InlineData("different-version", "differs from the coordinated producer version")]
    public void ValidateSubject_RejectsInvalidFirstPartyClosure(string mutation, string diagnostic)
    {
        var subject = MinimalSubject();
        var package = subject.FirstPartyPackages.Single();
        subject = mutation switch
        {
            "null" => subject with { FirstPartyPackages = null! },
            "empty" => subject with { FirstPartyPackages = [] },
            "missing-tailwind" => subject with
            {
                FirstPartyPackages = [package with { PackageId = "ForgeTrust.Other", ArtifactFileName = "other.nupkg" }]
            },
            _ => subject with { FirstPartyPackages = [package with { PackageVersion = "1.2.4-ci.798" }] }
        };

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(subject));

        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void ValidateSubject_RejectsMalformedManifestDigest(string? digest)
    {
        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ValidateSubject(
            MinimalSubject() with { ArtifactManifestSha256 = digest! }));

        Assert.Contains("artifactManifestSha256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_UsesDefaultForBlankInputAndResolvesRelativeAndAbsolutePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "tailwind-resolve-path");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "default.json")), TailwindProofSubjectService.ResolvePath(null, root, "default.json"));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "default.json")), TailwindProofSubjectService.ResolvePath("  ", root, "default.json"));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "reports", "proof.json")), TailwindProofSubjectService.ResolvePath(Path.Combine("reports", "proof.json"), root, "default.json"));
        var absolute = Path.GetFullPath(Path.Combine(root, "outside.json"));
        Assert.Equal(absolute, TailwindProofSubjectService.ResolvePath(absolute, root, "default.json"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown-package")]
    [InlineData("duplicate-package")]
    [InlineData("wrong-package-version")]
    public async Task Create_RejectsInvalidResolvedProducerClosure(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        IReadOnlyList<TailwindSubjectPackage> closure = mutation switch
        {
            "empty" => [],
            "unknown-package" => [new TailwindSubjectPackage("ForgeTrust.Other", Version, "other.nupkg", new string('e', 128)), fixture.Package],
            "duplicate-package" => [fixture.Package, fixture.Package],
            _ => [fixture.Package with { PackageVersion = "1.2.4-ci.798" }]
        };

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), closure, CancellationToken.None));

        Assert.Contains(mutation switch
        {
            "empty" => "missing Tailwind",
            "unknown-package" => "not in the producer plan",
            "duplicate-package" => "Duplicate resolved first-party package",
            _ => "does not match the coordinated producer plan"
        }, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RejectsResolvedClosureArchiveBytesThatDifferFromManifest()
    {
        var fixture = await CreateFixtureAsync();
        const string additionalPackageId = "ForgeTrust.AppSurface.Web";
        const string additionalPackageFileName = "ForgeTrust.AppSurface.Web.1.2.3-ci.798.nupkg";
        var additionalPackagePath = TestPathUtils.PathUnder(_artifacts, additionalPackageFileName);
        await File.WriteAllTextAsync(additionalPackagePath, "package bytes bound by producer manifest");
        var additionalPackageHash = PackageHash.ComputeSha512(additionalPackagePath);
        var manifest = new PackageArtifactManifest(
            1,
            Version,
            DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            [
                new PackageArtifactManifestEntry(TailwindId, "Web/Tailwind.csproj", "publish", fixture.Package.ArtifactFileName, fixture.Package.PackageSha512, false),
                new PackageArtifactManifestEntry(additionalPackageId, "Web/ForgeTrust.AppSurface.Web.csproj", "publish", additionalPackageFileName, additionalPackageHash, false)
            ]);
        await File.WriteAllBytesAsync(fixture.ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        await File.AppendAllTextAsync(additionalPackagePath, "changed after the manifest was frozen");

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40),
            [fixture.Package, new TailwindSubjectPackage(additionalPackageId, Version, additionalPackageFileName, additionalPackageHash)],
            CancellationToken.None));

        Assert.Contains("raw SHA-512 differs from the producer manifest", error.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("missing-sections")]
    [InlineData("malformed-key")]
    [InlineData("duplicate-property")]
    public async Task ReadResolvedClosure_RejectsInvalidAssetsRootAndPackageShape(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var json = mutation switch
        {
            "missing-sections" => "{\"libraries\":{},\"project\":{}}",
            "malformed-key" => Assets("net10.0").Replace($"{TailwindId}/{Version}", TailwindId, StringComparison.Ordinal),
            _ => Assets("net10.0").Replace("\"type\":\"package\"", "\"type\":\"package\",\"type\":\"package\"", StringComparison.Ordinal)
        };
        var path = TestPathUtils.PathUnder(_root, $"{mutation}.assets.json");
        await File.WriteAllTextAsync(path, json);

        if (mutation == "duplicate-property")
        {
            var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
            Assert.Contains("duplicate property", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));
        }
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsPlanFilenameThatDoesNotMatchCoordinatedVersion()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var entry = manifest.Entries.Single();
        var alteredManifest = manifest with
        {
            Entries = [entry with { ArtifactFileName = "renamed-package.nupkg" }]
        };
        var path = TestPathUtils.PathUnder(_root, "valid-assets-invalid-plan.json");
        await File.WriteAllTextAsync(path, Assets("net10.0"));

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, alteredManifest));

        Assert.Contains("filename", error.Message, StringComparison.Ordinal);
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
    public async Task ReadResolvedClosure_AcceptsKnownFirstPartyDependencyWithMatchingResolvedVersion()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        const string dependencyId = "ForgeTrust.AppSurface.Web";
        var dependencyFileName = $"{dependencyId}.{Version}.nupkg";
        var dependencyHash = new string('f', 128);
        manifest = manifest with
        {
            Entries = manifest.Entries.Append(new PackageArtifactManifestEntry(
                dependencyId, "Web/ForgeTrust.AppSurface.Web.csproj", "publish", dependencyFileName, dependencyHash, false)).ToArray()
        };

        var assets = JsonNode.Parse(Assets("net10.0"))!.AsObject();
        var target = assets["targets"]!["net10.0"]!.AsObject();
        target[$"{TailwindId}/{Version}"]!["dependencies"] = new JsonObject { [dependencyId] = Version };
        target.Add($"{dependencyId}/{Version}", new JsonObject { ["type"] = "package" });
        assets["libraries"]!.AsObject().Add($"{dependencyId}/{Version}", new JsonObject { ["type"] = "package" });

        var path = TestPathUtils.PathUnder(_root, "first-party-dependency.assets.json");
        await File.WriteAllTextAsync(path, assets.ToJsonString());

        var closure = TailwindProofSubjectService.ReadResolvedClosure(path, manifest);
        var expected = new[]
        {
            fixture.Package,
            new TailwindSubjectPackage(dependencyId, Version, dependencyFileName, dependencyHash)
        }.OrderBy(item => item.PackageId, StringComparer.Ordinal);

        Assert.Equal(expected, closure);
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsKnownFirstPartyDependencyWhenMetadataVersionDisagreesWithResolvedNode()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        const string dependencyId = "ForgeTrust.AppSurface.Web";
        var dependencyFileName = $"{dependencyId}.{Version}.nupkg";
        manifest = manifest with
        {
            Entries = manifest.Entries.Append(new PackageArtifactManifestEntry(
                dependencyId, "Web/ForgeTrust.AppSurface.Web.csproj", "publish", dependencyFileName, new string('f', 128), false)).ToArray()
        };

        var assets = JsonNode.Parse(Assets("net10.0"))!.AsObject();
        var target = assets["targets"]!["net10.0"]!.AsObject();
        target[$"{TailwindId}/{Version}"]!["dependencies"] = new JsonObject { [dependencyId] = "9.9.9" };
        target.Add($"{dependencyId}/{Version}", new JsonObject { ["type"] = "package" });
        assets["libraries"]!.AsObject().Add($"{dependencyId}/{Version}", new JsonObject { ["type"] = "package" });
        var path = TestPathUtils.PathUnder(_root, "wrong-first-party-dependency-version.assets.json");
        await File.WriteAllTextAsync(path, assets.ToJsonString());

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));

        Assert.Contains("First-party dependency version metadata", error.Message, StringComparison.Ordinal);
        Assert.Contains(dependencyId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsMissingAssetsFile()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = TestPathUtils.PathUnder(_root, "missing-project.assets.json");

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));

        Assert.Contains("did not produce", error.Message, StringComparison.Ordinal);
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
    [InlineData("non-string-dependency")]
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

    [Fact]
    public async Task Create_RejectsArchiveWithoutRequiredReleaseManifest()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            archive.GetEntry("build/tailwind.release.json")!.Delete();
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, PackageHash.ComputeSha512(packagePath));

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None));

        Assert.Contains("missing build/tailwind.release.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_AcceptsExplicitZipDirectoryEntriesThatContainPackageFiles()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            archive.CreateEntry("build/");
        var packageHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, packageHash);

        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40),
            [fixture.Package with { PackageSha512 = packageHash }], CancellationToken.None);

        Assert.Equal(packageHash, subject.PackageSha512);
        Assert.Equal(TailwindId, subject.FirstPartyPackages.Single().PackageId);
    }

    [Fact]
    public async Task Create_RejectsTailwindReleaseManifestOverDocumentLimitInsideZip()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            archive.GetEntry("build/tailwind.release.json")!.Delete();
            var releaseManifest = archive.CreateEntry("build/tailwind.release.json", CompressionLevel.Optimal);
            await using var stream = releaseManifest.Open();
            var chunk = Enumerable.Repeat((byte)'x', 64 * 1024).ToArray();
            var remaining = TailwindProofSubjectService.MaximumDocumentBytes + 1;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, chunk.Length);
                await stream.WriteAsync(chunk.AsMemory(0, count));
                remaining -= count;
            }
        }

        var packageHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, packageHash);
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40),
            [fixture.Package with { PackageSha512 = packageHash }], CancellationToken.None));

        Assert.Contains("Tailwind release manifest exceeds the 16 MiB document limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_RejectsArchiveWithoutRequiredReleaseManifest()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            archive.GetEntry("build/tailwind.release.json")!.Delete();
        var packageHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, packageHash);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.ManifestPath);
        var subject = MinimalSubject() with
        {
            ArtifactManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            PackageSha512 = packageHash,
            FirstPartyPackages = [fixture.Package with { PackageSha512 = packageHash }]
        };
        var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, _artifacts, CancellationToken.None);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName), _artifacts, fixture.ManifestPath,
            subjectHash, "1", "2", new string('a', 40), "3", CancellationToken.None));

        Assert.Contains("missing build/tailwind.release.json", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("missing-property")]
    [InlineData("non-object")]
    public async Task Validate_RejectsMalformedSubjectJsonShape(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var subject = await TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), [fixture.Package], CancellationToken.None);
        var json = JsonSerializer.Serialize(subject);
        json = mutation switch
        {
            "duplicate-property" => json.Insert(1, "\"schema\":\"appsurface-tailwind-proof-subject-v1\","),
            "unknown-property" => json.Insert(1, "\"unexpected\":true,"),
            "missing-property" => json.Replace(",\"payloadProjectionVersion\":1", string.Empty, StringComparison.Ordinal),
            _ => "[]"
        };
        var bytes = Encoding.UTF8.GetBytes(json);
        var subjectPath = TestPathUtils.PathUnder(_artifacts, TailwindProofSubjectService.FileName);
        await File.WriteAllBytesAsync(subjectPath, bytes);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.ValidateAsync(
            subjectPath, _artifacts, fixture.ManifestPath, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "12345", "98765", new string('a', 40), "222", CancellationToken.None));

        Assert.Contains(mutation switch
        {
            "duplicate-property" => "duplicate property",
            "unknown-property" => "unknown property",
            "missing-property" => "missing one or more required properties",
            _ => "must be a JSON object"
        }, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("case-collision")]
    [InlineData("symlink")]
    public async Task Create_RejectsUnsafeArchiveEntryTypes(string mutation)
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            if (mutation == "case-collision")
                Add(archive, "BUILD/TAILWIND.RELEASE.JSON", TailwindReleaseManifest());
            else
            {
                var symlink = archive.CreateEntry("build/linked-file");
                symlink.ExternalAttributes = (0xA000 << 16) | 0x1FF;
                using var writer = new StreamWriter(symlink.Open(), Encoding.UTF8);
                writer.Write("target");
            }
        }
        var changedHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, changedHash);
        TailwindSubjectPackage[] closure = [fixture.Package with { PackageSha512 = changedHash }];

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), closure, CancellationToken.None));

        Assert.Contains(mutation == "case-collision" ? "duplicate or case-colliding" : "symlink", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_RejectsFileThatIsAncestorOfAnotherArchiveEntry()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            Add(archive, "build", "file where a directory is required");
        var changedHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, changedHash);
        TailwindSubjectPackage[] closure = [fixture.Package with { PackageSha512 = changedHash }];

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40), closure, CancellationToken.None));

        Assert.Contains("file/directory collision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_RejectsArchiveEntryWhenEarlierFileOccupiesItsParentDirectory()
    {
        var fixture = await CreateFixtureAsync();
        var packagePath = TestPathUtils.PathUnder(_artifacts, fixture.Package.ArtifactFileName);
        File.Delete(packagePath);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            Add(archive, "build", "parent path is a regular file");
            Add(archive, $"{TailwindId}.nuspec", $"<package><metadata><id>{TailwindId}</id><version>{Version}</version></metadata></package>");
            Add(archive, "build/tailwind.release.json", TailwindReleaseManifest());
        }
        var changedHash = PackageHash.ComputeSha512(packagePath);
        await RewriteManifestHashAsync(fixture.ManifestPath, fixture.Package, changedHash);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => TailwindProofSubjectService.CreateAsync(
            _artifacts, fixture.ManifestPath, "12345", "98765", "1", new string('a', 40),
            [fixture.Package with { PackageSha512 = changedHash }], CancellationToken.None));

        Assert.Contains("file/directory collision", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsMissingAssetsSectionsAndMalformedPackageKeys()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var missingSections = TestPathUtils.PathUnder(_root, "missing-sections.assets.json");
        await File.WriteAllTextAsync(missingSections, "{\"libraries\":{},\"project\":{}}");
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(missingSections, manifest));

        var malformedKey = TestPathUtils.PathUnder(_root, "malformed-key.assets.json");
        await File.WriteAllTextAsync(malformedKey, Assets("net10.0").Replace($"{TailwindId}/{Version}", TailwindId, StringComparison.Ordinal));
        Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(malformedKey, manifest));
    }

    [Fact]
    public async Task ReadResolvedClosure_RejectsNonStringFirstPartyDependencyVersion()
    {
        var fixture = await CreateFixtureAsync();
        var manifest = await new PackageArtifactManifestReader().ReadAsync(fixture.ManifestPath, CancellationToken.None);
        var path = TestPathUtils.PathUnder(_root, "non-string-dependency.assets.json");
        await File.WriteAllTextAsync(path, Assets("net10.0", mutation: "non-string-dependency"));

        var error = Assert.Throws<PackageIndexException>(() => TailwindProofSubjectService.ReadResolvedClosure(path, manifest));

        Assert.Contains("version metadata", error.Message, StringComparison.Ordinal);
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
                "non-string-dependency" => (object)new Dictionary<string, object> { [TailwindId] = 1 },
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

    private static async Task RewriteManifestHashAsync(string manifestPath, TailwindSubjectPackage package, string hash)
    {
        var manifest = new PackageArtifactManifest(
            1,
            Version,
            DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            [new PackageArtifactManifestEntry(package.PackageId, "Web/Tailwind.csproj", "publish", package.ArtifactFileName, hash, false)]);
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
