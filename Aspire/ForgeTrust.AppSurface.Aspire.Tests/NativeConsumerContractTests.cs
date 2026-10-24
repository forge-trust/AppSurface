using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeConsumerContractTests
{
    [Fact]
    public void SettingsContract_RejectsMissingRequiredValuesAndInvalidBudgets()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var valid = Settings(root);

        foreach (var invalid in new[]
        {
            valid with { RepositoryRoot = " " },
            valid with { PackagePath = "" },
            valid with { PackageVersion = " " },
            valid with { DotnetPath = "" },
            valid with { ScratchParent = " " },
            valid with { EvidenceDirectory = "" },
        })
        {
            Assert.Throws<ArgumentException>(() => InstalledConsumerProducer.ValidateSettingsForTests(invalid));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => InstalledConsumerProducer.ValidateSettingsForTests(valid with { MaximumScratchBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstalledConsumerProducer.ValidateSettingsForTests(valid with { MinimumFreeBytes = -1 }));
        InstalledConsumerProducer.ValidateSettingsForTests(valid);
        Assert.Throws<ArgumentNullException>(() => InstalledConsumerProducer.ValidateSettingsForTests(null!));
    }

    [Fact]
    public async Task ExternalProfile_WithMissingSource_IsRejectedBeforeCandidateJourney()
    {
        var parent = Directory.CreateTempSubdirectory("appsurface-native-invalid-profile-").FullName;
        try
        {
            var settings = Settings(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory), parent) with
            {
                Consumer = new ConsumerValidationOptions(SourceDirectory: Path.Join(parent, "does-not-exist")),
            };
            await using var producer = new InstalledConsumerProducer(settings);
            var declaration = Declaration(producer.Id);
            var context = Context(declaration);

            var result = await producer.ProduceAsync(context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Contains("source", result.Diagnostic, StringComparison.Ordinal);
            Assert.Empty(result.SatisfiedAssertionIds);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task ExternalProfile_WithWrongSourceCommit_IsRejectedWithoutInstallingCandidate()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var fixture = Path.Join(root, "tools", "ForgeTrust.AppSurface.Evidence.Validation", "fixtures", "consumer");
        var parent = Directory.CreateTempSubdirectory("appsurface-native-wrong-commit-").FullName;
        try
        {
            var settings = Settings(root, parent) with
            {
                PackagePath = Path.Join(parent, "candidate.nupkg"),
                Consumer = new ConsumerValidationOptions(SourceDirectory: fixture, ExpectedCommit: new string('0', 40)),
            };
            await using var producer = new InstalledConsumerProducer(settings);
            var result = await producer.ProduceAsync(Context(Declaration(producer.Id)), CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Contains("source", result.Diagnostic, StringComparison.Ordinal);
            Assert.False(File.Exists(settings.PackagePath));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void SourcePathContract_RejectsRootedTraversalAndDriveLikePaths()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "consumer-root"));
        foreach (var path in new[] { Path.GetFullPath(Path.Combine(Path.GetTempPath(), "outside")), "../outside", "nested/../../outside", "C:\\outside" })
        {
            Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ContainedSourcePathForTests(root, path));
        }

        Assert.Equal(Path.Join(root, "tests", "Consumer.Tests.csproj"),
            InstalledConsumerProducer.ContainedSourcePathForTests(root, "tests/Consumer.Tests.csproj"));
    }

    [Fact]
    public void JUnitContract_RequiresIndependentSuiteCounts()
    {
        var passing = XElement.Parse("<testsuite tests='2' failures='1' errors='1' skipped='0'><testcase name='failed'><failure /></testcase><testcase name='errored'><error /></testcase></testsuite>");
        var cases = passing.Elements("testcase").ToArray();
        InstalledConsumerProducer.ValidateJUnitCountsForTests(passing, cases);

        foreach (var attribute in new[] { "tests", "failures", "errors", "skipped" })
        {
            var invalid = new XElement(passing);
            invalid.SetAttributeValue(attribute, attribute == "skipped" ? "1" : "0");
            Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ValidateJUnitCountsForTests(invalid, invalid.Elements("testcase").ToArray()));
        }

        var suites = XElement.Parse("<testsuites><testsuite tests='1' failures='0' errors='0' skipped='0'><testcase name='ok' /></testsuite></testsuites>");
        InstalledConsumerProducer.ValidateJUnitCountsForTests(suites, suites.Descendants("testcase").ToArray());
    }

    [Fact]
    public void SkipAndRequiredFragmentContracts_EnforceRationaleAndPassingEvidence()
    {
        var passing = XElement.Parse("<testcase name='AlphaDecisionTests.allows alpha' />");
        var skipped = XElement.Parse("<testcase name='BetaDecisionTests.requires service'><skipped /></testcase>");
        var failed = XElement.Parse("<testcase name='GammaDecisionTests.bad'><failure /></testcase>");

        Assert.True(InstalledConsumerProducer.IsPermittedSkipForTests(passing, null));
        Assert.False(InstalledConsumerProducer.IsPermittedSkipForTests(skipped, null));
        Assert.False(InstalledConsumerProducer.IsPermittedSkipForTests(skipped, new Dictionary<string, string>()));
        Assert.False(InstalledConsumerProducer.IsPermittedSkipForTests(skipped, new Dictionary<string, string> { ["service"] = " " }));
        Assert.False(InstalledConsumerProducer.IsPermittedSkipForTests(skipped, new Dictionary<string, string> { ["other"] = "reason" }));
        Assert.True(InstalledConsumerProducer.IsPermittedSkipForTests(skipped, new Dictionary<string, string> { ["BETADECISIONTESTS"] = "external dependency" }));

        InstalledConsumerProducer.ValidateRequiredTestFragmentsForTests(["AlphaDecisionTests"], [passing]);
        Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ValidateRequiredTestFragmentsForTests(["Missing"], [passing]));
        Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ValidateRequiredTestFragmentsForTests(["BetaDecisionTests"], [skipped]));
        Assert.Throws<InvalidDataException>(() => InstalledConsumerProducer.ValidateRequiredTestFragmentsForTests(["GammaDecisionTests"], [failed]));
    }

    [Fact]
    public void ProjectInventoryContract_HandlesSlnxSlnAndMalformedOrEmptyInventories()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-project-inventory-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "Alpha.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Join(root, "Beta.csproj"), "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            File.WriteAllText(Path.Join(root, "Consumer.slnx"), "<Solution><Project Path='Alpha.Tests.csproj' /><Project Path='Beta.csproj' /></Solution>");
            File.WriteAllText(Path.Join(root, "Consumer.sln"), "Project(\"{FAKE}\") = \"Alpha\", \"Alpha.Tests.csproj\", \"{A}\"\nEndProject");

            Assert.Equal(2, InstalledConsumerProducer.CountTestProjectsForTests(root, "Consumer.slnx"));
            Assert.Equal(1, InstalledConsumerProducer.CountTestProjectsForTests(root, "Consumer.sln"));
            Assert.True(InstalledConsumerProducer.IsTestProjectForTests(Path.Join(root, "Alpha.Tests.csproj")));
            Assert.True(InstalledConsumerProducer.IsTestProjectForTests(Path.Join(root, "Beta.csproj")));

            File.WriteAllText(Path.Join(root, "empty.slnx"), "<Solution />");
            Assert.Throws<InvalidOperationException>(() => InstalledConsumerProducer.CountTestProjectsForTests(root, "empty.slnx"));
            File.WriteAllText(Path.Join(root, "unknown.txt"), "not a solution");
            Assert.Throws<InvalidOperationException>(() => InstalledConsumerProducer.CountTestProjectsForTests(root, "unknown.txt"));
            Assert.Throws<FileNotFoundException>(() => InstalledConsumerProducer.CountTestProjectsForTests(root, "missing.slnx"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CandidatePackageContract_RejectsIdVersionAndSourceCommitMismatches()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-package-contract-").FullName;
        try
        {
            foreach (var metadata in new[]
            {
                ("Other.Package", "1.2.3", "commit"),
                ("ForgeTrust.AppSurface.Cli", "9.9.9", "commit"),
                ("ForgeTrust.AppSurface.Cli", "1.2.3", "wrong-commit"),
            })
            {
                using var archive = CreatePackage(root, metadata.Item1, metadata.Item2, metadata.Item3);
                Assert.Throws<InvalidOperationException>(() => ValidationProof.VerifyPackage(archive, Settings(root)));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GateReportContract_RequiresZeroToleranceThresholdsAndPatchThresholds()
    {
        var root = Directory.CreateTempSubdirectory("appsurface-native-gate-contract-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "coverage-gate.json"), GateJson(patch: true));
            InstalledConsumerProducer.ValidateGateReportForTests(root, hasPatch: true);

            foreach (var replacement in new[] { ("\"passed\":true", "\"passed\":false"), ("\"tolerancePercent\":0", "\"tolerancePercent\":1"), ("\"patchLine\":95", "\"patchLine\":94") })
            {
                File.WriteAllText(Path.Join(root, "coverage-gate.json"), GateJson(patch: true).Replace(replacement.Item1, replacement.Item2, StringComparison.Ordinal));
                Assert.Throws<InvalidOperationException>(() => InstalledConsumerProducer.ValidateGateReportForTests(root, hasPatch: true));
            }

            File.WriteAllText(Path.Join(root, "coverage-gate.json"), GateJson(patch: false));
            InstalledConsumerProducer.ValidateGateReportForTests(root, hasPatch: false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ValidationSettings Settings(string root, string? parent = null) => new(
        root, Path.Join(parent ?? root, "candidate.nupkg"), "1.2.3", "commit", "/missing/dotnet", parent ?? root, parent ?? root);

    private static EvidenceProducerDeclaration Declaration(string id) => new(id, "native-validation", "1", [], ["consumer/package-identity@1"], [new("result", "proof", "application/json", true, 1024)], 900);

    private static EvidenceProducerContext Context(EvidenceProducerDeclaration declaration) => new(new EvidencePlan("1", "p", "pd", "diff", new("p", EvidenceProfileScope.Targeted, [], [declaration], []), [], [], "digest"), declaration, TimeProvider.System);

    private static ZipArchive CreatePackage(string root, string id, string version, string commit)
    {
        var path = Path.Join(root, Guid.NewGuid() + ".nupkg");
        var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("package.nuspec");
        using (var writer = new StreamWriter(entry.Open()))
        {
            writer.Write($"<package><metadata><id>{id}</id><version>{version}</version><repository commit='{commit}' /></metadata></package>");
        }
        archive.Dispose();
        return ZipFile.Open(path, ZipArchiveMode.Read);
    }

    private static string GateJson(bool patch) => JsonSerializer.Serialize(new
    {
        passed = true, tolerancePercent = 0,
        thresholds = new { line = 95, branch = 85, patchLine = patch ? 95 : (int?)null, patchBranch = patch ? 85 : (int?)null },
        effectiveThresholds = new { line = 95, branch = 85, patchLine = patch ? 95 : (int?)null, patchBranch = patch ? 85 : (int?)null },
    });
}
