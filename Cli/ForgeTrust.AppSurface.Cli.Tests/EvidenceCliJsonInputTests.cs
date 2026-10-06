using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCliJsonInputTests
{
    [Theory]
    [InlineData("policy", false)]
    [InlineData("policy", true)]
    [InlineData("plan", false)]
    [InlineData("plan", true)]
    [InlineData("manifest", false)]
    [InlineData("manifest", true)]
    public async Task Workflow_CountsActualOpenedJsonBytesAtAndBeyondTheLimit(string input, bool oversized)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        var targetPath = paths[input];
        var original = await File.ReadAllBytesAsync(targetPath);
        var replacement = new byte[EvidenceCanonicalJson.MaximumInputBytes + (oversized ? 1 : 0)];
        Array.Fill(replacement, (byte)' ');
        original.CopyTo(replacement, 0);
        var access = new ReplacingInputAccess(targetPath, replacement);
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner(), jsonFileAccess: access);

        async Task ExecuteAsync()
        {
            if (input == "policy")
            {
                var report = await workflow.DoctorAsync(
                    new EvidencePlanningRequest(paths["policy"], ["docs/readme.md"], null), CancellationToken.None);
                Assert.Equal("unverified", Assert.Single(report.Checks, check => check.Id == "trusted-envelope").Status);
            }
            else
            {
                var (plan, manifest) = await workflow.VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None);
                Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            }
        }

        if (oversized)
        {
            var exception = await Assert.ThrowsAsync<EvidenceCliException>(ExecuteAsync);
            Assert.Equal(input == "policy" ? "ASEVD205" : "ASEVD209", exception.Code);
            Assert.Contains("20 MiB", exception.Message, StringComparison.Ordinal);
            Assert.Contains("bounded-json-input", exception.Fix, StringComparison.Ordinal);
        }
        else
        {
            await ExecuteAsync();
        }

        Assert.True(access.WasReplaced);
        Assert.Equal(replacement.Length, new FileInfo(targetPath).Length);
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
        Assert.Empty(Directory.GetDirectories(directory.Path));
    }

    [Theory]
    [InlineData("{\"Id\":\"json-secret-canary\",\"id\":\"duplicate\"}")]
    [InlineData("{\"Id\":\"json-secret-canary\",\"Profiles\":[{\"Scope\":0}]}")]
    [InlineData("{\"Id\":\"json-secret-canary\",\"Profiles\":[{\"Scope\":\"unknown\"}]}")]
    [InlineData("null")]
    public async Task Doctor_RejectsAmbiguousOrInvalidPolicyWithSafeDiagnostic(string json)
    {
        using var directory = TestDirectory.Create();
        var policyPath = Path.Join(directory.Path, "policy.json");
        await File.WriteAllTextAsync(policyPath, json);

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(() =>
            new EvidenceCliWorkflow(new EvidencePlanner()).DoctorAsync(
                new EvidencePlanningRequest(policyPath, ["docs/readme.md"], null), CancellationToken.None));

        Assert.Equal("ASEVD205", exception.Code);
        Assert.DoesNotContain("json-secret-canary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unique properties", exception.Fix, StringComparison.Ordinal);
        Assert.Equal([policyPath], Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("plan", "ASEVD209")]
    [InlineData("manifest", "ASEVD209")]
    public async Task Verify_RejectsUnsupportedSchemaBeforeBindingWithSafeDiagnostic(string input, string code)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        var json = await File.ReadAllTextAsync(paths[input]);
        await File.WriteAllTextAsync(paths[input], json.Replace(
            "\"ContractVersion\":\"1.0\"", "\"ContractVersion\":\"json-secret-canary\"", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(() =>
            new EvidenceCliWorkflow(new EvidencePlanner()).VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None));

        Assert.Equal(code, exception.Code);
        Assert.DoesNotContain("json-secret-canary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("current supported schema", exception.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("plan")]
    [InlineData("manifest")]
    public async Task Workflow_DoesNotEchoInputOpenFailure(string input)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        File.Delete(paths[input]);
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner(), jsonFileAccess: new FailingInputAccess(paths[input]));

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(async () =>
        {
            if (input == "policy")
            {
                await workflow.DoctorAsync(
                    new EvidencePlanningRequest(paths["policy"], ["docs/readme.md"], null), CancellationToken.None);
            }
            else
            {
                await workflow.VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None);
            }
        });

        Assert.Equal(input == "policy" ? "ASEVD205" : "ASEVD209", exception.Code);
        Assert.DoesNotContain("json-secret-canary", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("policy", false)]
    [InlineData("plan", false)]
    [InlineData("manifest", false)]
    [InlineData("policy", true)]
    [InlineData("plan", true)]
    [InlineData("manifest", true)]
    public async Task Workflow_MapsMissingInputsWhenOpeningFiles(string input, bool missingDirectory)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        File.Delete(paths[input]);
        if (missingDirectory)
        {
            paths[input] = Path.Join(directory.Path, "missing-directory", input + ".json");
        }

        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var exception = await Assert.ThrowsAsync<EvidenceCliException>(async () =>
        {
            if (input == "policy")
            {
                await workflow.DoctorAsync(
                    new EvidencePlanningRequest(paths["policy"], ["docs/readme.md"], null), CancellationToken.None);
            }
            else
            {
                await workflow.VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None);
            }
        });

        Assert.Equal(input == "policy" ? "ASEVD204" : "ASEVD208", exception.Code);
    }

    [Theory]
    [InlineData("policy", "")]
    [InlineData("plan", "")]
    [InlineData("manifest", "")]
    [InlineData("policy", "path-secret-canary\0.json")]
    [InlineData("plan", "path-secret-canary\0.json")]
    [InlineData("manifest", "path-secret-canary\0.json")]
    public async Task Workflow_MapsInvalidPathsToSafeDiagnostic(string input, string path)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        paths[input] = path;
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(async () =>
        {
            if (input == "policy")
            {
                await workflow.DoctorAsync(
                    new EvidencePlanningRequest(paths["policy"], ["docs/readme.md"], null), CancellationToken.None);
            }
            else
            {
                await workflow.VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None);
            }
        });

        Assert.Equal(input == "policy" ? "ASEVD204" : "ASEVD208", exception.Code);
        Assert.DoesNotContain("path-secret-canary", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Theory]
    [InlineData("policy", false)]
    [InlineData("plan", false)]
    [InlineData("manifest", false)]
    [InlineData("policy", true)]
    [InlineData("plan", true)]
    [InlineData("manifest", true)]
    public async Task Workflow_MapsInvalidPropertyEncodingToSafeDiagnostic(string input, bool escapedSurrogate)
    {
        using var directory = TestDirectory.Create();
        var paths = await CreateInputsAsync(directory.Path);
        byte[] property = escapedSurrogate ? "\\uD800"u8.ToArray() : [0xff];
        byte[] json = [.. "{\""u8.ToArray(), .. property, .. "\":\"unicode-secret-canary\"}"u8.ToArray()];
        await File.WriteAllBytesAsync(paths[input], json);

        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var exception = await Assert.ThrowsAsync<EvidenceCliException>(async () =>
        {
            if (input == "policy")
            {
                await workflow.DoctorAsync(
                    new EvidencePlanningRequest(paths["policy"], ["docs/readme.md"], null), CancellationToken.None);
            }
            else
            {
                await workflow.VerifyAsync(paths["plan"], paths["manifest"], CancellationToken.None);
            }
        });

        Assert.Equal(input == "policy" ? "ASEVD205" : "ASEVD209", exception.Code);
        Assert.DoesNotContain("unicode-secret-canary", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    private static async Task<Dictionary<string, string>> CreateInputsAsync(string root)
    {
        var profile = new EvidenceProfile("docs", EvidenceProfileScope.Targeted, [], [], []);
        var conservative = new EvidenceProfile(
            "contract", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("contract", "contract", "1", [], ["contract/assertion@1"], [], 1)],
            [new EvidenceObligation("contract", "contract", "Contract behavior changed.", ["contract"], "contract/assertion@1")]);
        var policy = new EvidencePolicy(
            "bounded-input", "1", "contract", [profile, conservative],
            [new EvidencePolicyRule("docs", "docs/**", "docs")]);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("docs/readme.md")]);
        var manifest = EvidenceAdmissionTestFixture.BuildLegacyStructural(plan, []);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["policy"] = Path.Join(root, "policy.json"),
            ["plan"] = Path.Join(root, "plan.json"),
            ["manifest"] = Path.Join(root, "manifest.json"),
        };
        await File.WriteAllBytesAsync(paths["policy"], EvidenceCanonicalJson.Serialize(policy));
        await File.WriteAllBytesAsync(paths["plan"], EvidenceCanonicalJson.Serialize(plan));
        await File.WriteAllBytesAsync(paths["manifest"], EvidenceCanonicalJson.Serialize(manifest));
        return paths;
    }

    private sealed class ReplacingInputAccess(string targetPath, byte[] replacement) : IEvidenceInputFileAccess
    {
        public bool WasReplaced { get; private set; }

        public Stream OpenRead(string path)
        {
            if (string.Equals(path, targetPath, StringComparison.Ordinal))
            {
                File.WriteAllBytes(path, replacement);
                WasReplaced = true;
            }

            return File.OpenRead(path);
        }
    }

    private sealed class FailingInputAccess(string targetPath) : IEvidenceInputFileAccess
    {
        public Stream OpenRead(string path) => string.Equals(path, targetPath, StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException("json-secret-canary")
            : File.OpenRead(path);
    }
}
