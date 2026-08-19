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
    [InlineData("malformed-manifest", "CPV002")]
    [InlineData("unsupported-schema", "CPV004")]
    [InlineData("wrong-package", "CPV005")]
    [InlineData("wrong-class", "CPV006")]
    [InlineData("wrong-filename", "CPV007")]
    [InlineData("unsafe-filename", "CPV007")]
    [InlineData("uncovered-sign", "CPV008")]
    [InlineData("uncovered-branch", "CPV009")]
    [InlineData("duplicate-sign-condition", "CPV009")]
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
            "private reproduce command",
            CoverageCliConsumerProofSemanticProof.NotRun);

        using var document = JsonDocument.Parse(CoverageCliConsumerProofEvidenceRenderer.RenderJson(report));

        Assert.Equal("failed", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("not-run", document.RootElement.GetProperty("raw").GetProperty("outcome").GetString());
        Assert.False(document.RootElement.TryGetProperty("packageArtifactDigest", out _));
        Assert.False(document.RootElement.GetProperty("raw").TryGetProperty("artifactRelativePath", out _));
        Assert.False(document.RootElement.GetProperty("raw").TryGetProperty("sha256", out _));
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
                case "malformed-manifest":
                    File.WriteAllText(ManifestPath, "{");
                    break;
                case "unsupported-schema":
                    File.WriteAllText(CoveragePath, "<coverage><unexpected /></coverage>");
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
