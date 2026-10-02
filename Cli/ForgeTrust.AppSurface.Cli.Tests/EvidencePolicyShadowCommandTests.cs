using System.Text.Json;
using System.Text.Json.Serialization;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class EvidencePolicyShadowCommandTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Command_Should_Write_CamelCase_NonClaiming_Result_To_New_Output_Directory()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var outputDirectory = TestPathUtils.PathUnder(temporaryDirectory.Path, "shadow-output");

        var run = await InvokeAsync(CreateArguments(inputs, outputDirectory));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("claimEligible=false", run.Output, StringComparison.Ordinal);
        var outputPath = TestPathUtils.PathUnder(outputDirectory, "evidence-policy-shadow.json");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        var root = document.RootElement;
        Assert.False(root.GetProperty("claimEligible").GetBoolean());
        Assert.False(root.TryGetProperty("manifest", out _));
        Assert.False(root.TryGetProperty("evidenceManifest", out _));
        Assert.True(root.GetProperty("result").GetProperty("isCompatible").GetBoolean());
        var selection = Assert.Single(root.GetProperty("result").GetProperty("selections").EnumerateArray());
        Assert.Equal("docs/guide.md", selection.GetProperty("changedPath").GetProperty("path").GetString());
        Assert.Contains("\"changedPath\"", await File.ReadAllTextAsync(inputs.BaseFixturesPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_Should_Treat_Missing_Candidate_Fixtures_As_Empty_And_Report_Base_Deletion()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-deleted-");
        var fixture = Fixture("required-code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");
        var inputs = await WriteInputsAsync(temporaryDirectory.Path, Fixtures(fixture), candidateFixturesJson: null);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "deleted-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI010", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isCompatible").GetBoolean());
        Assert.Contains(
            result.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "ASEPS004");
        Assert.Contains(
            result.GetProperty("selections").EnumerateArray(),
            selection => selection.GetProperty("fixtureSource").GetString() == "Base");
    }

    [Fact]
    public async Task Command_Should_Return_Nonzero_And_Report_Weakened_Candidate_Selection()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-weakened-");
        var fixture = Fixture("database", EvidencePolicyShadowFixtureKind.Resource, "resources/migrations/001.sql");
        var candidatePolicy = CreateWeakenedPolicy(CreatePolicy());
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture),
            candidatePolicy: candidatePolicy);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "weakened-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPS007", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isCompatible").GetBoolean());
        Assert.Contains(
            result.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "ASEPS007");
        Assert.False(document.RootElement.GetProperty("claimEligible").GetBoolean());
    }

    [Fact]
    public async Task Command_Should_Fail_Closed_On_Malformed_Candidate_Fixtures_Without_Rendering_Raw_Text()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-malformed-");
        const string hostileText = "candidate-owned-RAW-SENTINEL-###";
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            hostileText);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "malformed-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI002", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileText, run.AllText, StringComparison.Ordinal);
        var outputText = await File.ReadAllTextAsync(outputPath);
        Assert.DoesNotContain(hostileText, outputText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(outputText);
        Assert.False(document.RootElement.GetProperty("claimEligible").GetBoolean());
        Assert.Equal(
            "ASEPSCLI002",
            Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray())
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task Command_Should_Fail_Closed_When_Any_Input_Exceeds_The_Byte_Limit()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-oversized-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        await File.WriteAllBytesAsync(inputs.BasePolicyPath, new byte[(1024 * 1024) + 1]);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "oversized-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI003", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        var root = document.RootElement;
        Assert.False(root.GetProperty("claimEligible").GetBoolean());
        Assert.False(root.GetProperty("result").GetProperty("isCompatible").GetBoolean());
        Assert.Equal(
            "ASEPSCLI003",
            Assert.Single(root.GetProperty("result").GetProperty("diagnostics").EnumerateArray())
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task Command_Should_Reject_A_Missing_Base_Fixture_File()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-missing-base-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            baseFixturesJson: null,
            Fixtures(fixture));
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "missing-base-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI001", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        Assert.False(document.RootElement.GetProperty("result").GetProperty("isCompatible").GetBoolean());
        Assert.Contains(
            document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("fixtureSource").GetString() == "Base");
    }

    private static string[] CreateArguments(CommandInputs inputs, string outputPath) =>
    [
        "evidence", "shadow-policy",
        "--base-policy", inputs.BasePolicyPath,
        "--candidate-policy", inputs.CandidatePolicyPath,
        "--base-fixtures", inputs.BaseFixturesPath,
        "--candidate-fixtures", inputs.CandidateFixturesPath,
        "--output", outputPath,
    ];

    private static async Task<CommandInputs> WriteInputsAsync(
        string root,
        string? baseFixturesJson,
        string? candidateFixturesJson,
        EvidencePolicy? basePolicy = null,
        EvidencePolicy? candidatePolicy = null)
    {
        var basePolicyPath = TestPathUtils.PathUnder(root, "base-policy.json");
        var candidatePolicyPath = TestPathUtils.PathUnder(root, "candidate-policy.json");
        var baseFixturesPath = TestPathUtils.PathUnder(root, "base-fixtures.json");
        var candidateFixturesPath = TestPathUtils.PathUnder(root, "candidate-fixtures.json");
        await File.WriteAllBytesAsync(basePolicyPath, EvidenceCanonicalJson.Serialize(basePolicy ?? CreatePolicy()));
        await File.WriteAllBytesAsync(candidatePolicyPath, EvidenceCanonicalJson.Serialize(candidatePolicy ?? CreatePolicy()));
        if (baseFixturesJson is not null)
        {
            await File.WriteAllTextAsync(baseFixturesPath, baseFixturesJson);
        }

        if (candidateFixturesJson is not null)
        {
            await File.WriteAllTextAsync(candidateFixturesPath, candidateFixturesJson);
        }

        return new CommandInputs(basePolicyPath, candidatePolicyPath, baseFixturesPath, candidateFixturesPath);
    }

    private static string Fixtures(params EvidencePolicyShadowFixture[] fixtures) =>
        JsonSerializer.Serialize(fixtures, FixtureJsonOptions);

    private static EvidencePolicyShadowFixture Fixture(
        string id,
        EvidencePolicyShadowFixtureKind kind,
        string path) =>
        new(id, kind, new NormalizedDiffPath(path));

    private static async Task<CapturedCommand> InvokeAsync(string[] args)
    {
        using var console = new FakeInMemoryConsole();
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await AppSurfaceCliApp.RunAsync(
                args,
                options => options.CustomRegistrations.Add(
                    services => services.AddSingleton<IConsole>(console)));
            var output = console.ReadOutputString();
            var error = console.ReadErrorString();
            return new CapturedCommand(output, error, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    private static EvidencePolicy CreatePolicy()
    {
        var documentation = new EvidenceProfile("documentation", EvidenceProfileScope.Targeted, [], [], []);
        var code = CreateRequirementProfile("code", "code", hasResource: false);
        var resource = CreateRequirementProfile("resource", "resource", hasResource: true);
        var control = CreateRequirementProfile("control", "control", hasResource: true);
        var conservative = new EvidenceProfile(
            "conservative",
            EvidenceProfileScope.Targeted,
            [.. code.Resources, .. resource.Resources, .. control.Resources],
            [.. code.Producers, .. resource.Producers, .. control.Producers],
            [.. code.Obligations, .. resource.Obligations, .. control.Obligations]);

        return new EvidencePolicy(
            "shadow-command-test",
            "1",
            "conservative",
            [conservative, documentation, code, resource, control],
            [
                new EvidencePolicyRule("docs", "docs/**", documentation.Id),
                new EvidencePolicyRule("code", "src/**", code.Id),
                new EvidencePolicyRule("resource", "resources/**", resource.Id),
                new EvidencePolicyRule("control", ".github/**", control.Id),
            ]);
    }

    private static EvidencePolicy CreateWeakenedPolicy(EvidencePolicy policy) =>
        policy with
        {
            ConservativeProfileId = "code",
            Rules =
            [
                .. policy.Rules.Where(static rule => rule.Id is "docs" or "code"),
                new EvidencePolicyRule("resource", "resources/**", "documentation"),
            ],
        };

    private static EvidenceProfile CreateRequirementProfile(string profileId, string prefix, bool hasResource)
    {
        var resourceId = $"{prefix}-database";
        var producerId = $"{prefix}-tests";
        var assertionId = $"{prefix}/passed@1";
        var resources = hasResource
            ? new[] { new EvidenceResourceDeclaration(resourceId, "aspire_health", 30, []) }
            : [];
        var producer = new EvidenceProducerDeclaration(
            producerId,
            "integration-tests",
            "1.0.0",
            hasResource ? [resourceId] : [],
            [assertionId],
            [new EvidenceArtifactSlot("report", prefix, "application/json", Required: true, MaximumBytes: 1024)],
            60);
        var obligation = new EvidenceObligation(
            $"{prefix}-obligation",
            "integration",
            $"The {prefix} profile must pass.",
            [producerId],
            assertionId);

        return new EvidenceProfile(profileId, EvidenceProfileScope.Targeted, resources, [producer], [obligation]);
    }

    private sealed record CommandInputs(
        string BasePolicyPath,
        string CandidatePolicyPath,
        string BaseFixturesPath,
        string CandidateFixturesPath);

    private sealed record CapturedCommand(string Output, string Error, int ExitCode)
    {
        public string AllText => Output + Error;
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = TestPathUtils.PathUnder(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                new DirectoryInfo(path).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }

            return new TempDirectory(path);
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
