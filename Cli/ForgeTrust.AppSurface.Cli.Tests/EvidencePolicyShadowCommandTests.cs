using System.Text;
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
        const string hostileText = "oversized-input-RAW-SENTINEL";
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var oversizedInput = new byte[(1024 * 1024) + 1];
        Encoding.UTF8.GetBytes(hostileText).CopyTo(oversizedInput, 0);
        await File.WriteAllBytesAsync(inputs.BasePolicyPath, oversizedInput);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "oversized-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI003", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(inputs.BasePolicyPath, run.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileText, run.AllText, StringComparison.Ordinal);
        var outputText = await File.ReadAllTextAsync(outputPath);
        Assert.DoesNotContain(inputs.BasePolicyPath, outputText, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileText, outputText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(outputText);
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

    [Theory]
    [InlineData(true, "Base", "--base-policy")]
    [InlineData(false, "Candidate", "--candidate-policy")]
    public async Task Command_Should_Reject_A_Missing_Policy_File_With_Redacted_Diagnostic(
        bool isBasePolicy,
        string expectedSource,
        string optionName)
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-missing-policy-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var missingPolicyPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "private-missing-policy.json");
        inputs = isBasePolicy
            ? inputs with { BasePolicyPath = missingPolicyPath }
            : inputs with { CandidatePolicyPath = missingPolicyPath };
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "missing-policy-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI001", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(missingPolicyPath, run.AllText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ASEPSCLI001", diagnostic.GetProperty("code").GetString());
        Assert.Equal(expectedSource, diagnostic.GetProperty("fixtureSource").GetString());
        Assert.Equal(
            $"{optionName} could not be read; no comparison was performed.",
            diagnostic.GetProperty("message").GetString());
        Assert.DoesNotContain(missingPolicyPath, document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Base", "--base-policy")]
    [InlineData(false, "Candidate", "--candidate-policy")]
    public async Task Command_Should_Reject_Malformed_Policy_Without_Leaking_Path_Or_Raw_Text(
        bool isBasePolicy,
        string expectedSource,
        string optionName)
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-malformed-policy-");
        const string hostileText = "policy-owned-RAW-SENTINEL-###";
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var policyPath = isBasePolicy ? inputs.BasePolicyPath : inputs.CandidatePolicyPath;
        await File.WriteAllTextAsync(policyPath, hostileText);
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "malformed-policy-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI002", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileText, run.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(policyPath, run.AllText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ASEPSCLI002", diagnostic.GetProperty("code").GetString());
        Assert.Equal(expectedSource, diagnostic.GetProperty("fixtureSource").GetString());
        Assert.Equal(
            $"{optionName} is malformed Evidence JSON; no comparison was performed.",
            diagnostic.GetProperty("message").GetString());
        var outputText = document.RootElement.GetRawText();
        Assert.DoesNotContain(hostileText, outputText, StringComparison.Ordinal);
        Assert.DoesNotContain(policyPath, outputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_Should_Reject_A_Directory_As_A_Policy_Input_With_Redacted_Diagnostic()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-directory-input-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var policyDirectory = Directory.CreateDirectory(
            TestPathUtils.PathUnder(temporaryDirectory.Path, "policy-input-directory")).FullName;
        inputs = inputs with { BasePolicyPath = policyDirectory };
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "directory-policy-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI001", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(policyDirectory, run.AllText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ASEPSCLI001", diagnostic.GetProperty("code").GetString());
        Assert.Equal("Base", diagnostic.GetProperty("fixtureSource").GetString());
        Assert.Equal(
            "--base-policy could not be read; no comparison was performed.",
            diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Command_Should_Classify_Null_Candidate_Fixtures_As_Malformed_Without_Leaking_The_Path()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-null-policy-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            "null");
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "null-candidate-fixtures-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI002", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(inputs.CandidateFixturesPath, run.AllText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ASEPSCLI002", diagnostic.GetProperty("code").GetString());
        Assert.Equal("Candidate", diagnostic.GetProperty("fixtureSource").GetString());
        Assert.Equal(
            "--candidate-fixtures is malformed Evidence JSON; no comparison was performed.",
            diagnostic.GetProperty("message").GetString());
        Assert.DoesNotContain(inputs.CandidateFixturesPath, document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_Should_Treat_Candidate_Fixtures_With_Missing_Parent_Directory_As_Empty()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-missing-fixture-parent-");
        var fixture = Fixture("required-code", EvidencePolicyShadowFixtureKind.Code, "src/Feature.cs");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        inputs = inputs with
        {
            CandidateFixturesPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "missing-parent", "candidate-fixtures.json")
        };
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "missing-fixture-parent-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI010", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath));
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isCompatible").GetBoolean());
        Assert.Contains(
            result.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == "ASEPS004");
    }

    [Fact]
    public async Task Command_Should_Reject_Blank_Base_Policy_Path_With_Redacted_Diagnostic()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-blank-policy-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        inputs = inputs with { BasePolicyPath = string.Empty };
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "blank-policy-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI001", run.Error, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var diagnostic = Assert.Single(document.RootElement.GetProperty("result").GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("ASEPSCLI001", diagnostic.GetProperty("code").GetString());
        Assert.Equal("Base", diagnostic.GetProperty("fixtureSource").GetString());
        Assert.Equal(
            "--base-policy must name an input file; no comparison was performed.",
            diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Command_Should_Map_Missing_Output_Option_To_Redacted_Write_Error()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-missing-output-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));

        var run = await InvokeAsync(CreateArgumentsWithoutOutput(inputs));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI006", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(temporaryDirectory.Path, run.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_Should_Map_Output_Beneath_A_File_To_Redacted_Write_Error()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-unsafe-output-");
        const string blockerContents = "output-parent-KEEP-CONTENTS";
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var outputParentFile = TestPathUtils.PathUnder(temporaryDirectory.Path, "output-parent-file");
        await File.WriteAllTextAsync(outputParentFile, blockerContents);
        var unsafeOutputPath = TestPathUtils.PathUnder(outputParentFile, "shadow-result.json");

        var run = await InvokeAsync(CreateArguments(inputs, unsafeOutputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI006: The bounded shadow result could not be written to a new output target.", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(outputParentFile, run.AllText, StringComparison.Ordinal);
        Assert.Equal(blockerContents, await File.ReadAllTextAsync(outputParentFile));
        Assert.False(File.Exists(unsafeOutputPath));
    }

    [Fact]
    public async Task Command_Should_Honor_PreCanceled_Console_Without_Creating_Output()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-canceled-");
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "canceled-result.json");
        using var console = new FakeConsole(new MemoryStream(), new MemoryStream(), new MemoryStream());
        var cancellationToken = console.RegisterCancellationHandler();
        console.RequestCancellation(TimeSpan.Zero);
        Assert.True(cancellationToken.IsCancellationRequested);

        var originalExitCode = Environment.ExitCode;
        try
        {
            try
            {
                await AppSurfaceCliApp.RunAsync(
                    CreateArguments(inputs, outputPath),
                    options => options.CustomRegistrations.Add(
                        services => services.AddSingleton<IConsole>(console)));
            }
            catch (OperationCanceledException)
            {
                // The command may propagate cancellation or let CliFx handle the canceled execution.
            }
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Command_Should_Refuse_To_Overwrite_Existing_Output_And_Preserve_Contents()
    {
        using var temporaryDirectory = TempDirectory.Create("appsurface-policy-shadow-existing-output-");
        const string existingContents = "output-owned-KEEP-EXISTING-CONTENTS";
        var fixture = Fixture("docs", EvidencePolicyShadowFixtureKind.Documentation, "docs/guide.md");
        var inputs = await WriteInputsAsync(
            temporaryDirectory.Path,
            Fixtures(fixture),
            Fixtures(fixture));
        var outputPath = TestPathUtils.PathUnder(temporaryDirectory.Path, "existing-result.json");
        await File.WriteAllTextAsync(outputPath, existingContents);

        var run = await InvokeAsync(CreateArguments(inputs, outputPath));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("ASEPSCLI006: The bounded shadow result could not be written to a new output target.", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(outputPath, run.AllText, StringComparison.Ordinal);
        Assert.Equal(existingContents, await File.ReadAllTextAsync(outputPath));
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

    private static string[] CreateArgumentsWithoutOutput(CommandInputs inputs) =>
    [
        "evidence", "shadow-policy",
        "--base-policy", inputs.BasePolicyPath,
        "--candidate-policy", inputs.CandidatePolicyPath,
        "--base-fixtures", inputs.BaseFixturesPath,
        "--candidate-fixtures", inputs.CandidateFixturesPath,
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
