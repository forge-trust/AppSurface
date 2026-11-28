using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>
/// Keeps the Evidence CLI README in step with the preview <c>test</c> producer: its policy fragment must load through
/// the planner, and its reason and receipt tables must list every reason the code can report.
/// </summary>
public sealed class EvidenceTestProducerReadmeContractTests
{
    private static readonly string Readme = File.ReadAllText(TestPathUtils.PathUnder(
        TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
        "Evidence",
        "ForgeTrust.AppSurface.Evidence.Cli",
        "README.md")).ReplaceLineEndings("\n");

    [Fact]
    public async Task FirstRunFragment_ShouldValidateAndSelectTheTestProfileWhenMergedIntoTheStarterPolicy()
    {
        using var directory = TestDirectory.Create();
        var root = Path.Join(directory.Path, ".appsurface", "evidence");
        await new EvidenceCliWorkflow(new EvidencePlanner()).InitializeAsync(root, force: false, CancellationToken.None);
        var policyJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(root, "evidence.policy.json")))!.AsObject();
        var profileText = Fragment("test-producer-profile");
        policyJson["profiles"]!.AsArray().Add(JsonNode.Parse(profileText));
        policyJson["rules"]!.AsArray().Add(JsonNode.Parse(Fragment("test-producer-rule")));

        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(Encoding.UTF8.GetBytes(policyJson.ToJsonString()));
        EvidencePlanner.ValidatePolicy(policy);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);

        Assert.Equal("tooling-tests", plan.Profile.Id);
        var producer = Assert.Single(plan.Profile.Producers);
        Assert.Equal(EvidenceTestProducerContract.Kind, producer.Kind);
        Assert.Equal("tests/REPLACE-ME.Tests/REPLACE-ME.Tests.csproj", Assert.Single(producer.Test!.Projects).Path);
        Assert.Equal(["tooling-tests-complete"], plan.Profile.Obligations.Select(static obligation => obligation.Id));
        Assert.Single(profileText.Split('\n'), static line => line.Contains("REPLACE-ME", StringComparison.Ordinal));
        Assert.Contains("tests/REPLACE-ME.Tests/REPLACE-ME.Tests.csproj | filter: (none, every test) | minimumTests: 1", EvidenceCliWorkflow.FormatSummary(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void ReasonTable_ShouldListEveryReasonWithItsAnchorOutcomeAndNextAction()
    {
        foreach (var reason in Enum.GetValues<TestRunReason>())
        {
            var row = $"| <a id=\"{TestEvidenceRunSupport.Anchor(reason)}\"></a>`{reason}` | `{TestRunReasons.Describe(reason).Outcome}` | ";
            var line = Assert.Single(Readme.Split('\n'), candidate => candidate.StartsWith(row, StringComparison.Ordinal));
            Assert.EndsWith($"| {TestEvidenceRunSupport.DescribeNextAction(reason)} |", line.TrimEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReasonTable_ShouldFollowThePrecedenceOrderOfTheEnum()
    {
        var documented = Readme.Split('\n')
            .Where(static line => line.StartsWith("| <a id=\"reason-", StringComparison.Ordinal))
            .Select(static line => line.Split('`')[1])
            .ToArray();

        Assert.Equal(Enum.GetNames<TestRunReason>(), documented);
    }

    [Fact]
    public void ReceiptFieldTable_ShouldListEveryReasonAndEveryReceiptField()
    {
        var reasonRow = Assert.Single(Readme.Split('\n'), static line => line.StartsWith("| `projects[].reason` |", StringComparison.Ordinal));
        foreach (var reason in Enum.GetNames<TestRunReason>())
        {
            Assert.Contains($"`{reason}`", reasonRow, StringComparison.Ordinal);
        }

        using var receipt = System.Text.Json.JsonDocument.Parse(TestEvidenceProducer.SerializeReceipt(
        [
            new TestProjectOutcome("a.csproj", "00", 1, TestRunCounts.Zero, TestRunReason.Complete, null, "reconcile", [], null),
        ]));
        Assert.Contains("| `schemaVersion` |", Readme, StringComparison.Ordinal);
        foreach (var field in receipt.RootElement.GetProperty("projects")[0].EnumerateObject())
        {
            Assert.True(
                Readme.Contains($"`projects[].{field.Name}`", StringComparison.Ordinal) || Readme.Contains($"`{field.Name}`", StringComparison.Ordinal),
                $"The receipt field '{field.Name}' is not documented.");
        }
    }

    [Fact]
    public void Readme_ShouldCarryThePreviewAnchorTheCliPrintsAndTheRewordedClaims()
    {
        Assert.EndsWith("#test-producer-preview", TestEvidenceRunSupport.PreviewLine, StringComparison.Ordinal);
        Assert.Contains("\n## Test producer (preview)\n", Readme, StringComparison.Ordinal);
        Assert.Contains("A filtered test suite cannot close a repository-wide coverage assertion; it can close the [`test` assertion](#test-producer-preview)", Readme, StringComparison.Ordinal);
        Assert.Contains("or the [scoped patch coverage assertion](#scoped-patch-coverage) of a producer that names its projects and sources in the plan", Readme, StringComparison.Ordinal);
        Assert.Contains("it does not scan consumer assemblies, discover test projects,", Readme, StringComparison.Ordinal);
        Assert.DoesNotContain("A skipped test project, filtered test suite, unavailable browser", Readme, StringComparison.Ordinal);
        foreach (var code in new[] { "ASEVD203", "ASEVD208", "ASEVD209", "ASEVD213", "ASEVD214", "ASEVD215", "ASEVD216" })
        {
            Assert.Contains(code, Readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Readme_ShouldDocumentProvenanceAndGateFieldsInVerificationSection()
    {
        var sections = Readme.ReplaceLineEndings("\n").Split("\n## ", StringSplitOptions.None);
        var verification = Assert.Single(
            sections,
            static section => section.StartsWith("Verifying evidence\n", StringComparison.Ordinal));

        Assert.Contains("provenance", verification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`ClaimKind`", verification, StringComparison.Ordinal);
        Assert.Contains("`Eligibility`", verification, StringComparison.Ordinal);
    }

    private static string Fragment(string marker)
    {
        var begin = Readme.IndexOf($"<!-- {marker}: begin -->", StringComparison.Ordinal);
        var end = Readme.IndexOf($"<!-- {marker}: end -->", StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, $"README markers for '{marker}' are missing.");
        var block = Readme[begin..end];
        var start = block.IndexOf("```json", StringComparison.Ordinal) + "```json".Length;
        return block[start..block.LastIndexOf("```", StringComparison.Ordinal)].Trim();
    }
}
