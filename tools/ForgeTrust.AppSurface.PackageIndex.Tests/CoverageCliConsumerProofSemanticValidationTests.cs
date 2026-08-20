using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class CoverageCliConsumerProofSemanticValidationTests
{
    [Fact]
    public void ValidateRaw_BindsOnlyTheKnownManifestAndChecksSemanticFacts()
    {
        using var fixture = SemanticFixture.Create();

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.True(proof.CanMerge);
        Assert.Empty(proof.Failures);
        Assert.Equal("passed", proof.Raw.Outcome);
        Assert.Equal(CoverageCliConsumerProofSemanticValidator.ExpectedProjectPath, proof.RawArtifact!.ProjectPath);
        Assert.Matches("^[0-9a-f]{64}$", proof.RawArtifact.Sha256);
        Assert.Contains("branch:Calculator.Sign@7:100% (2/2):jump", proof.Raw.Invariants);
    }

    [Theory]
    [InlineData("missing-manifest", "CPV001")]
    [InlineData("manifest-directory", "CPV001")]
    [InlineData("malformed-manifest", "CPV002")]
    [InlineData("oversized-manifest", "CPV002")]
    [InlineData("unsafe-project-path", "CPV002")]
    [InlineData("control-project-path", "CPV002")]
    [InlineData("rooted-project-path", "CPV002")]
    [InlineData("backslash-project-path", "CPV002")]
    [InlineData("empty-project-path-segment", "CPV002")]
    [InlineData("unsafe-slug", "CPV002")]
    [InlineData("empty-slug", "CPV002")]
    [InlineData("mismatched-slug", "CPV002")]
    [InlineData("manifest-non-object", "CPV002")]
    [InlineData("unmatched-project", "CPV001")]
    [InlineData("missing-selected-coverage", "CPV001")]
    [InlineData("malformed-cobertura", "CPV002")]
    [InlineData("unsupported-schema", "CPV004")]
    [InlineData("wrong-cobertura-root", "CPV004")]
    [InlineData("invalid-cobertura-shape", "CPV004")]
    [InlineData("element-limit", "CPV004")]
    [InlineData("namespaced-cobertura", "CPV004")]
    [InlineData("no-cobertura-class", "CPV004")]
    [InlineData("unsupported-attribute", "CPV004")]
    [InlineData("namespaced-attribute", "CPV004")]
    [InlineData("missing-line-attribute", "CPV004")]
    [InlineData("invalid-line-grammar", "CPV004")]
    [InlineData("branch-without-condition-coverage", "CPV004")]
    [InlineData("invalid-condition-grammar", "CPV004")]
    [InlineData("invalid-condition-coverage", "CPV004")]
    [InlineData("zero-condition-total", "CPV004")]
    [InlineData("invalid-condition-coverage-syntax", "CPV004")]
    [InlineData("dtd-cobertura", "CPV004")]
    [InlineData("wrong-package", "CPV005")]
    [InlineData("wrong-class", "CPV006")]
    [InlineData("wrong-filename", "CPV007")]
    [InlineData("unsafe-filename", "CPV007")]
    [InlineData("filename-without-parent", "CPV007")]
    [InlineData("uncovered-sign", "CPV008")]
    [InlineData("uncovered-branch", "CPV009")]
    [InlineData("duplicate-sign-condition", "CPV009")]
    [InlineData("duplicate-sign-line", "CPV008")]
    [InlineData("nonjump-sign-condition", "CPV009")]
    [InlineData("uncovered-sign-jump-condition", "CPV009")]
    public void ValidateRaw_FailsClosedForManifestAndSemanticContractViolations(string scenario, string expectedCode)
    {
        using var fixture = SemanticFixture.Create();
        fixture.Apply(scenario);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.Contains(proof.Failures, failure => failure.Code == expectedCode);
        Assert.False(proof.Succeeded);
    }

    [Fact]
    public void ValidateRaw_RejectsAmbiguousMatchingManifests()
    {
        using var fixture = SemanticFixture.Create();
        var duplicateDirectory = Path.Join(fixture.CoverageRunDirectory, "projects", "Smoke.Tests-duplicate");
        Directory.CreateDirectory(duplicateDirectory);
        File.WriteAllText(
            Path.Join(duplicateDirectory, "coverage-project.json"),
            """
            { "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke.Tests-duplicate" }
            """);
        File.WriteAllText(Path.Join(duplicateDirectory, "coverage.cobertura.xml"), SemanticFixture.ValidCobertura);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV002");
        Assert.False(proof.CanMerge);
    }

    [Fact]
    public void ValidateRaw_RejectsLinkedProjectDirectory()
    {
        using var fixture = SemanticFixture.Create();
        var targetDirectory = Path.Join(fixture.Root, "linked-project-target");
        var linkedDirectory = Path.Join(fixture.CoverageRunDirectory, "projects", "linked-project");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV001" && failure.Cause.Contains("non-regular or linked", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRaw_ReportsUnreadableProjectManifestDirectory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = SemanticFixture.Create();
        var projectsDirectory = Path.Join(fixture.CoverageRunDirectory, "projects");
        var originalMode = File.GetUnixFileMode(projectsDirectory);
        try
        {
            File.SetUnixFileMode(projectsDirectory, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

            Assert.Contains(proof.Failures, failure => failure.Code == "CPV001" && failure.Cause.Contains("project manifests could not be read", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(projectsDirectory, originalMode);
        }
    }

    [Fact]
    public void ValidateRaw_ReportsUnreadableSelectedCoverageArtifact()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = SemanticFixture.Create();
        var originalMode = File.GetUnixFileMode(fixture.RawCoveragePath);
        try
        {
            File.SetUnixFileMode(fixture.RawCoveragePath, UnixFileMode.UserWrite);

            var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

            Assert.Contains(proof.Failures, failure => failure.Code == "CPV001" && failure.Cause.Contains("could not be read as a regular raw artifact", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(fixture.RawCoveragePath, originalMode);
        }
    }

    [Fact]
    public void ValidateRaw_RejectsDuplicateCoverageIdentities()
    {
        using var fixture = SemanticFixture.Create();
        File.WriteAllText(
            fixture.RawCoveragePath,
            SemanticFixture.ValidCobertura.Replace(
                "</packages>",
                """
                    <package name="Smoke" line-rate="1" branch-rate="1" complexity="1">
                      <classes><class name="Smoke.Calculator" filename="Smoke/Calculator.cs" line-rate="1" branch-rate="1" complexity="1" /></classes>
                    </package>
                  </packages>
                """,
                StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV010");
    }

    [Fact]
    public void ValidateRaw_AllowsAdditionalClassesInTheSingleExpectedPackage()
    {
        using var fixture = SemanticFixture.Create();
        File.WriteAllText(
            fixture.RawCoveragePath,
            SemanticFixture.ValidCobertura.Replace(
                "</classes>",
                "<class name=\"Smoke.Other\" filename=\"Smoke/Other.cs\" line-rate=\"1\" branch-rate=\"1\" complexity=\"1\" /></classes>",
                StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.True(proof.CanMerge);
        Assert.Empty(proof.Failures);
    }

    [Fact]
    public void ValidateRaw_AllowsALeadingRootInTheExpectedCalculatorFilename()
    {
        using var fixture = SemanticFixture.Create();
        File.WriteAllText(
            fixture.RawCoveragePath,
            SemanticFixture.ValidCobertura.Replace(
                "filename=\"Smoke/Calculator.cs\"",
                "filename=\"/Smoke/Calculator.cs\"",
                StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.True(proof.CanMerge);
        Assert.Empty(proof.Failures);
    }

    [Fact]
    public void ValidateRaw_AllowsAnAbsoluteFixtureFilenameWithTheExpectedSafeSuffix()
    {
        using var fixture = SemanticFixture.Create();
        File.WriteAllText(
            fixture.RawCoveragePath,
            SemanticFixture.ValidCobertura.Replace(
                "filename=\"Smoke/Calculator.cs\"",
                "filename=\"/private/fixture/Smoke/Calculator.cs\"",
                StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        Assert.True(proof.CanMerge);
        Assert.Empty(proof.Failures);
    }

    [Fact]
    public void ValidateMerged_ComparesSelectedRawFactsAndRejectsMergeLoss()
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);
        var merged = Path.Join(fixture.Root, "coverage-fan-in", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(merged)!);
        File.WriteAllText(merged, SemanticFixture.ValidCobertura.Replace("hits=\"2\"", "hits=\"0\"", StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, merged);

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV008" && failure.Scope == "merged");
        Assert.Contains(proof.Failures, failure => failure.Code == "CPV011");
        Assert.Equal("failed", proof.Merged.Outcome);
    }

    [Fact]
    public void ValidateMerged_RejectsMissingMergedReport()
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, Path.Join(fixture.Root, "missing", "coverage.cobertura.xml"));

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV003");
    }

    [Fact]
    public void ValidateMerged_RejectsAChangedMergeShard()
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);
        File.AppendAllText(shard, "\n");
        var merged = Path.Join(fixture.Root, "coverage-fan-in", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(merged)!);
        File.Copy(raw.RawArtifact.CoveragePath, merged);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, merged);

        Assert.False(proof.Succeeded);
        Assert.Contains(proof.Failures, failure => failure.Code == "CPV011" && failure.Scope == "raw-to-merged");
    }

    [Fact]
    public void ValidateMerged_RejectsAnOtherwiseValidMergeThatAddsSemanticFacts()
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);
        var merged = Path.Join(fixture.Root, "coverage-fan-in", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(merged)!);
        File.WriteAllText(
            merged,
            SemanticFixture.ValidCobertura.Replace(
                "<line number=\"5\" hits=\"2\" />",
                "<line number=\"5\" hits=\"2\" /><line number=\"9\" hits=\"1\" />",
                StringComparison.Ordinal));

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, merged);

        Assert.Contains(proof.Failures, failure => failure.Code == "CPV011" && failure.Scope == "merged");
        Assert.Equal("failed", proof.Merged.Outcome);
    }

    [Fact]
    public void ValidateMerged_ReturnsAnUnmergeableRawFailureUnchanged()
    {
        using var fixture = SemanticFixture.Create();
        fixture.Apply("missing-manifest");
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(
            raw,
            Path.Join(fixture.Root, "missing-shard.xml"),
            Path.Join(fixture.Root, "missing-merged.xml"));

        Assert.Same(raw, proof);
        Assert.Equal("not-run", proof.Merged.Outcome);
        Assert.DoesNotContain(proof.Failures, failure => failure.Code is "CPV003" or "CPV011");
    }

    [Fact]
    public void ValidateMerged_ReturnsAnUnreadableRawProofUnchanged()
    {
        using var fixture = SemanticFixture.Create();
        fixture.Apply("malformed-cobertura");
        var rawProof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(
            rawProof,
            Path.Join(fixture.Root, "missing-shard.xml"),
            Path.Join(fixture.Root, "missing-merged.xml"));

        Assert.Same(rawProof, proof);
        Assert.Equal("not-run", proof.Merged.Outcome);
    }

    [Fact]
    public void ValidateMerged_ReportsUnreadableCopiedShard()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = SemanticFixture.Create();
        var rawProof = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var copiedShardPath = Path.Join(fixture.Root, "copied-shard.xml");
        File.Copy(fixture.RawCoveragePath, copiedShardPath);
        var originalMode = File.GetUnixFileMode(copiedShardPath);
        try
        {
            File.SetUnixFileMode(copiedShardPath, UnixFileMode.UserWrite);

            var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(
                rawProof,
                copiedShardPath,
                Path.Join(fixture.Root, "missing-merged.xml"));

            Assert.Contains(proof.Failures, failure => failure.Code == "CPV011" && failure.Scope == "raw-to-merged");
        }
        finally
        {
            File.SetUnixFileMode(copiedShardPath, originalMode);
        }
    }

    [Theory]
    [InlineData("<coverage>")]
    [InlineData("<coverage><unexpected /></coverage>")]
    public void ValidateMerged_RejectsMalformedAndUnsupportedCobertura(string mergedCobertura)
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);
        var merged = Path.Join(fixture.Root, "coverage-fan-in", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(merged)!);
        File.WriteAllText(merged, mergedCobertura);

        var proof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, merged);

        Assert.Equal("failed", proof.Merged.Outcome);
        Assert.Contains(proof.Failures, failure => failure.Code == "CPV004" && failure.Scope == "merged");
    }

    [Fact]
    public void EvidenceRenderer_EmitsOnlyRelativeSemanticEvidence()
    {
        using var fixture = SemanticFixture.Create();
        var raw = CoverageCliConsumerProofSemanticValidator.ValidateRaw(fixture.CoverageRunDirectory);
        var shard = Path.Join(fixture.Root, "coverage-shards", "Smoke.Tests", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(shard)!);
        File.Copy(raw.RawArtifact!.CoveragePath, shard);
        var merged = Path.Join(fixture.Root, "coverage-fan-in", "coverage.cobertura.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(merged)!);
        File.Copy(raw.RawArtifact.CoveragePath, merged);
        var semanticProof = CoverageCliConsumerProofSemanticValidator.ValidateMerged(raw, shard, merged);
        var report = new CoverageCliConsumerProofReport(
            "0.0.0-ci.42",
            fixture.Root,
            "https://api.nuget.org/v3/index.json",
            new CoverageCliConsumerProofSelectedArtifact("ForgeTrust.AppSurface.Cli", "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "/private/package.nupkg", "appsurface", "artifact-digest"),
            "/private/tool.config",
            "/private/fixture.config",
            "/private/logs",
            [],
            [],
            string.Empty,
            "private reproduce command",
            semanticProof);

        var json = CoverageCliConsumerProofEvidenceRenderer.RenderJson(report);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("passed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("semantic-retention", document.RootElement.GetProperty("driverBoundary").GetProperty("assuranceLevel").GetString());
        Assert.Equal("consumer/TestResults/coverage-merged/projects/Smoke.Tests-123/coverage.cobertura.xml", document.RootElement.GetProperty("raw").GetProperty("artifactRelativePath").GetString());
        Assert.True(document.RootElement.GetProperty("raw").TryGetProperty("sha256", out _));
        Assert.False(document.RootElement.GetProperty("merged").TryGetProperty("sha256", out _));
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("private reproduce command", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceRenderer_FailsClosedWhenSemanticProofDidNotRun()
    {
        var report = new CoverageCliConsumerProofReport(
            "0.0.0-ci.42",
            "/private/work",
            "https://api.nuget.org/v3/index.json",
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            string.Empty,
            "private reproduce command");

        using var document = JsonDocument.Parse(CoverageCliConsumerProofEvidenceRenderer.RenderJson(report));

        Assert.Equal("failed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("not-run", document.RootElement.GetProperty("raw").GetProperty("outcome").GetString());
        Assert.False(document.RootElement.TryGetProperty("packageArtifactDigest", out _));
        Assert.False(document.RootElement.GetProperty("raw").TryGetProperty("artifactRelativePath", out _));
        Assert.False(document.RootElement.GetProperty("raw").TryGetProperty("sha256", out _));
    }

    [Fact]
    public void EvidenceRenderer_RendersOnlySafeSemanticFailurePaths()
    {
        var workDirectory = Path.Join(Path.GetTempPath(), "coverage-proof-work");
        var semanticProof = new CoverageCliConsumerProofSemanticProof(
            null,
            new CoverageCliConsumerProofSemanticOutcome(
                "failed",
                Path.Join(workDirectory, "consumer", "coverage.cobertura.xml"),
                null,
                [],
                null),
            new CoverageCliConsumerProofSemanticOutcome(
                "not-run",
                "bad\0path",
                null,
                [],
                null),
            [
                new CoverageCliConsumerProofFailure(
                    "CPV004",
                    "raw",
                    "The bounded Cobertura schema was rejected.",
                    "Regenerate the selected report.",
                    "../private/output.xml"),
                new CoverageCliConsumerProofFailure(
                    "CPV005",
                    "raw",
                    "The expected package was absent.",
                    "Exercise the known consumer fixture.",
                    "logs\\coverage.stdout.log"),
                new CoverageCliConsumerProofFailure(
                    "CPV006",
                    "raw",
                    "The expected class was absent.",
                    "Exercise the known consumer fixture.",
                    "logs/../../private/output.xml"),
            ]);
        var report = new CoverageCliConsumerProofReport(
            "0.0.0-ci.42",
            workDirectory,
            "https://api.nuget.org/v3/index.json",
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            string.Empty,
            "private reproduce command",
            semanticProof);

        using var document = JsonDocument.Parse(CoverageCliConsumerProofEvidenceRenderer.RenderJson(report));
        var failures = document.RootElement.GetProperty("failures");

        Assert.Equal("failed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("consumer/coverage.cobertura.xml", document.RootElement.GetProperty("raw").GetProperty("artifactRelativePath").GetString());
        Assert.Equal("unavailable", document.RootElement.GetProperty("merged").GetProperty("artifactRelativePath").GetString());
        Assert.Equal("unavailable", failures[0].GetProperty("evidenceRelativePath").GetString());
        Assert.Equal("logs/coverage.stdout.log", failures[1].GetProperty("evidenceRelativePath").GetString());
        Assert.Equal("unavailable", failures[2].GetProperty("evidenceRelativePath").GetString());
    }

    private sealed class SemanticFixture : IDisposable
    {
        private SemanticFixture(string root)
        {
            Root = root;
            CoverageRunDirectory = Path.Join(root, "consumer", "TestResults", "coverage-merged");
            ProjectDirectory = Path.Join(CoverageRunDirectory, "projects", "Smoke.Tests-123");
        }

        internal const string ValidCobertura = """
            <coverage line-rate="1" branch-rate="1" lines-covered="2" lines-valid="2" branches-covered="2" branches-valid="2" version="1" timestamp="0">
              <sources><source>.</source></sources>
              <packages>
                <package name="Smoke" line-rate="1" branch-rate="1" complexity="1">
                  <classes>
                    <class name="Smoke.Calculator" filename="Smoke/Calculator.cs" line-rate="1" branch-rate="1" complexity="1">
                      <methods>
                        <method name="Sign" signature="(System.Int32)" line-rate="1" branch-rate="1" complexity="1">
                          <lines><line number="7" hits="2" branch="True" condition-coverage="100% (2/2)"><conditions><condition number="0" type="jump" coverage="100%" /></conditions></line></lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="5" hits="2" />
                        <line number="7" hits="2" branch="True" condition-coverage="100% (2/2)"><conditions><condition number="0" type="jump" coverage="100%" /></conditions></line>
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        internal string Root { get; }

        internal string CoverageRunDirectory { get; }

        private string ProjectDirectory { get; }

        private string ManifestPath => Path.Join(ProjectDirectory, "coverage-project.json");

        private string CoveragePath => Path.Join(ProjectDirectory, "coverage.cobertura.xml");

        internal string RawCoveragePath => CoveragePath;

        internal static SemanticFixture Create()
        {
            var root = TestPathUtils.PathUnder(Path.GetTempPath(), "CoverageCliConsumerProofSemanticValidationTests", Guid.NewGuid().ToString("N"));
            var fixture = new SemanticFixture(root);
            Directory.CreateDirectory(fixture.ProjectDirectory);
            File.WriteAllText(
                fixture.ManifestPath,
                """
                { "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke.Tests-123" }
                """);
            File.WriteAllText(fixture.CoveragePath, ValidCobertura);
            return fixture;
        }

        internal void Apply(string scenario)
        {
            switch (scenario)
            {
                case "missing-manifest":
                    File.Delete(ManifestPath);
                    break;
                case "manifest-directory":
                    File.Delete(ManifestPath);
                    Directory.CreateDirectory(ManifestPath);
                    break;
                case "malformed-manifest":
                    File.WriteAllText(ManifestPath, "{");
                    break;
                case "oversized-manifest":
                    File.WriteAllText(ManifestPath, new string(' ', 16 * 1024 + 1));
                    break;
                case "unsafe-project-path":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "../Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "control-project-path":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke\u000A.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "rooted-project-path":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "/Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "backslash-project-path":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests\\Smoke.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "empty-project-path-segment":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests//Smoke.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "unsafe-slug":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke/Tests" }""");
                    break;
                case "empty-slug":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke.Tests.csproj", "slug": "" }""");
                    break;
                case "mismatched-slug":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Smoke.Tests/Smoke.Tests.csproj", "slug": "Smoke.Tests-other" }""");
                    break;
                case "manifest-non-object":
                    File.WriteAllText(ManifestPath, "[]");
                    break;
                case "unmatched-project":
                    File.WriteAllText(ManifestPath, """{ "schemaVersion": 1, "projectPath": "Other.Tests/Other.Tests.csproj", "slug": "Smoke.Tests-123" }""");
                    break;
                case "missing-selected-coverage":
                    File.Delete(CoveragePath);
                    break;
                case "malformed-cobertura":
                    File.WriteAllText(CoveragePath, "<coverage>");
                    break;
                case "unsupported-schema":
                    File.WriteAllText(CoveragePath, "<coverage><unexpected /></coverage>");
                    break;
                case "wrong-cobertura-root":
                    File.WriteAllText(CoveragePath, "<sources />");
                    break;
                case "invalid-cobertura-shape":
                    File.WriteAllText(CoveragePath, "<coverage><classes /></coverage>");
                    break;
                case "element-limit":
                    File.WriteAllText(CoveragePath, $"<coverage><sources>{string.Concat(Enumerable.Repeat("<source />", 10_001))}</sources></coverage>");
                    break;
                case "namespaced-cobertura":
                    File.WriteAllText(CoveragePath, "<coverage xmlns=\"urn:test\" />");
                    break;
                case "no-cobertura-class":
                    File.WriteAllText(CoveragePath, "<coverage><sources><source>.</source></sources><packages /></coverage>");
                    break;
                case "unsupported-attribute":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("<coverage ", "<coverage unsupported=\"true\" ", StringComparison.Ordinal));
                    break;
                case "namespaced-attribute":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("<coverage ", "<coverage xmlns:proof=\"urn:proof\" proof:version=\"1\" ", StringComparison.Ordinal));
                    break;
                case "missing-line-attribute":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("<line number=\"5\" hits=\"2\" />", "<line hits=\"2\" />", StringComparison.Ordinal));
                    break;
                case "invalid-line-grammar":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("number=\"5\"", "number=\"0\"", StringComparison.Ordinal));
                    break;
                case "branch-without-condition-coverage":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace(" condition-coverage=\"100% (2/2)\"", string.Empty, StringComparison.Ordinal));
                    break;
                case "invalid-condition-grammar":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("coverage=\"100%\"", "coverage=\"101%\"", StringComparison.Ordinal));
                    break;
                case "invalid-condition-coverage":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("100% (2/2)", "100% (3/2)", StringComparison.Ordinal));
                    break;
                case "zero-condition-total":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("100% (2/2)", "0% (0/0)", StringComparison.Ordinal));
                    break;
                case "invalid-condition-coverage-syntax":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("100% (2/2)", "complete", StringComparison.Ordinal));
                    break;
                case "dtd-cobertura":
                    File.WriteAllText(CoveragePath, "<!DOCTYPE coverage []><coverage />");
                    break;
                case "wrong-package":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("name=\"Smoke\"", "name=\"Other\"", StringComparison.Ordinal));
                    break;
                case "wrong-class":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("name=\"Smoke.Calculator\"", "name=\"Smoke.Other\"", StringComparison.Ordinal));
                    break;
                case "wrong-filename":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("filename=\"Smoke/Calculator.cs\"", "filename=\"Smoke/Other.cs\"", StringComparison.Ordinal));
                    break;
                case "unsafe-filename":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("filename=\"Smoke/Calculator.cs\"", "filename=\"../Smoke/Calculator.cs\"", StringComparison.Ordinal));
                    break;
                case "filename-without-parent":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("filename=\"Smoke/Calculator.cs\"", "filename=\"Calculator.cs\"", StringComparison.Ordinal));
                    break;
                case "uncovered-sign":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("number=\"7\" hits=\"2\"", "number=\"7\" hits=\"0\"", StringComparison.Ordinal));
                    break;
                case "uncovered-branch":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("condition-coverage=\"100% (2/2)\"", "condition-coverage=\"50% (1/2)\"", StringComparison.Ordinal));
                    break;
                case "duplicate-sign-condition":
                    File.WriteAllText(
                        CoveragePath,
                        ValidCobertura.Replace(
                            "<conditions><condition number=\"0\" type=\"jump\" coverage=\"100%\" /></conditions>",
                            "<conditions><condition number=\"0\" type=\"jump\" coverage=\"100%\" /><condition number=\"1\" type=\"jump\" coverage=\"100%\" /></conditions>",
                            StringComparison.Ordinal));
                    break;
                case "duplicate-sign-line":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("<line number=\"5\" hits=\"2\" />", "<line number=\"7\" hits=\"2\" />", StringComparison.Ordinal));
                    break;
                case "nonjump-sign-condition":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("type=\"jump\"", "type=\"switch\"", StringComparison.Ordinal));
                    break;
                case "uncovered-sign-jump-condition":
                    File.WriteAllText(CoveragePath, ValidCobertura.Replace("coverage=\"100%\"", "coverage=\"0%\"", StringComparison.Ordinal));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown semantic proof test scenario.");
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
