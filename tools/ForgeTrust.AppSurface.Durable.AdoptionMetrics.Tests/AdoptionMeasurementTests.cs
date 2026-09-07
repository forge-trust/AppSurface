using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.Durable.AdoptionMetrics.Tests;

public sealed class AdoptionMeasurementTests : IDisposable
{
    private const string Commit = "1111111111111111111111111111111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AppSurfaceDurableAdoptionMetricsTests",
        Guid.NewGuid().ToString("N"));

    public AdoptionMeasurementTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task MeasureAsyncCountsPhysicalLinesAndOrdersResultsDeterministically()
    {
        var fixture = await CreateValidFixtureAsync(reverseRegions: true);
        var verifier = new RecordingRevisionVerifier();

        var result = await AdoptionMeasurementEngine.MeasureAsync(
            fixture.SpecPath,
            fixture.ConsumerRoot,
            fixture.RepositoryRoot,
            verifier,
            CancellationToken.None);

        Assert.True(result.OverallPassed);
        Assert.Equal(6, result.Regions.Count);
        Assert.Collection(
            result.Regions,
            region => AssertRegion(region, "external-activation-mapping", AdoptionVariant.Baseline, 2),
            region => AssertRegion(region, "external-activation-mapping", AdoptionVariant.Proposed, 2),
            region => AssertRegion(region, "primary-lifecycle-test", AdoptionVariant.Baseline, 2),
            region => AssertRegion(region, "primary-lifecycle-test", AdoptionVariant.Proposed, 2),
            region => AssertRegion(region, "registration", AdoptionVariant.Baseline, 2),
            region => AssertRegion(region, "registration", AdoptionVariant.Proposed, 2));
        Assert.Equal(fixture.ConsumerRoot, verifier.ConsumerRoot);
        Assert.Equal(Commit, verifier.ExpectedCommit);
        Assert.Equal(
            [
                "consumer-external-activation-mapping.cs",
                "consumer-primary-lifecycle-test.cs",
                "consumer-registration.cs",
            ],
            verifier.SelectedRelativePaths);
    }

    [Fact]
    public async Task WriterProducesStableCamelCaseJsonWithoutBom()
    {
        var fixture = await CreateValidFixtureAsync();
        var result = await AdoptionMeasurementEngine.MeasureAsync(
            fixture.SpecPath,
            fixture.ConsumerRoot,
            fixture.RepositoryRoot,
            new RecordingRevisionVerifier(),
            CancellationToken.None);
        var output = Path.Combine(_root, "nested", "result.json");

        await AdoptionMeasurementWriter.WriteAsync(output, result, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(output, CancellationToken.None);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.Contains("\"variant\": \"baseline\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceRoot\": \"consumer\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-start", "startToken matched 0 lines")]
    [InlineData("duplicate-start", "startToken matched 2 lines")]
    [InlineData("missing-end", "endToken matched 0 lines")]
    [InlineData("duplicate-end", "endToken matched 2 lines")]
    [InlineData("reversed", "startToken must occur before endToken")]
    public async Task MeasureAsyncRejectsInvalidTokenTopology(string mutation, string expected)
    {
        var fixture = await CreateValidFixtureAsync();
        var targetPath = Path.Combine(fixture.RepositoryRoot, "repository-registration.cs");
        var content = mutation switch
        {
            "missing-start" => "wrong-start\none\ntwo\nend",
            "duplicate-start" => "start\nstart\none\ntwo\nend",
            "missing-end" => "start\none\ntwo\nwrong-end",
            "duplicate-end" => "start\none\ntwo\nend\nend",
            "reversed" => "end\none\ntwo\nstart",
            _ => throw new InvalidOperationException("Unexpected test mutation."),
        };
        await File.WriteAllTextAsync(targetPath, content, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("absolute", "relativePath must not be rooted")]
    [InlineData("escape", "relativePath escapes its declared source root")]
    [InlineData("missing", "does not exist")]
    public async Task MeasureAsyncRejectsUnsafeOrMissingPaths(string mutation, string expected)
    {
        var fixture = await CreateValidFixtureAsync();
        await RewriteSpecAsync(
            fixture.SpecPath,
            root =>
            {
                var region = GetRegions(root)[5]!.AsObject();
                region["relativePath"] = mutation switch
                {
                    "absolute" => Path.Combine(_root, "outside.cs"),
                    "escape" => "../outside.cs",
                    "missing" => "missing.cs",
                    _ => throw new InvalidOperationException("Unexpected test mutation."),
                };
            });

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("line-count", "expected 3 nonblank lines but measured 2")]
    [InlineData("region-passed", "expected passed=false but measured passed=true")]
    [InlineData("overall-passed", "expected overallPassed=false but measured overallPassed=true")]
    public async Task MeasureAsyncRejectsStaleExpectedResults(string mutation, string expected)
    {
        var fixture = await CreateValidFixtureAsync();
        await RewriteSpecAsync(
            fixture.SpecPath,
            root =>
            {
                if (mutation == "line-count")
                {
                    GetRegions(root)[5]!["lineCount"] = 3;
                }
                else if (mutation == "region-passed")
                {
                    GetRegions(root)[5]!["passed"] = false;
                }
                else
                {
                    root["overallPassed"] = false;
                }
            });

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schema", "Unsupported schemaVersion")]
    [InlineData("repository", "consumerRepository is required")]
    [InlineData("commit", "baselineCommit must be a full 40-character Git commit")]
    [InlineData("count", "exactly 6 regions")]
    [InlineData("unexpected-name", "requires exactly one baseline and one proposed entry")]
    [InlineData("duplicate-variant", "requires exactly one baseline and one proposed entry")]
    [InlineData("blank-token", "Every region requires")]
    [InlineData("multiline-token", "tokens must each identify one physical line")]
    [InlineData("negative-count", "lineCount cannot be negative")]
    [InlineData("zero-limit", "limit must be positive")]
    public async Task MeasureAsyncRejectsInvalidSpecifications(string mutation, string expected)
    {
        var fixture = await CreateValidFixtureAsync();
        await RewriteSpecAsync(
            fixture.SpecPath,
            root =>
            {
                switch (mutation)
                {
                    case "schema":
                        root["schemaVersion"] = 2;
                        break;
                    case "repository":
                        root["consumerRepository"] = " ";
                        break;
                    case "commit":
                        root["baselineCommit"] = "abc";
                        break;
                    case "count":
                        GetRegions(root).RemoveAt(0);
                        break;
                    case "unexpected-name":
                        GetRegions(root)[0]!["name"] = "other";
                        break;
                    case "duplicate-variant":
                        GetRegions(root)[1]!["variant"] = "baseline";
                        break;
                    case "blank-token":
                        GetRegions(root)[0]!["startToken"] = "";
                        break;
                    case "multiline-token":
                        GetRegions(root)[0]!["startToken"] = "start\nother";
                        break;
                    case "negative-count":
                        GetRegions(root)[0]!["lineCount"] = -1;
                        break;
                    case "zero-limit":
                        GetRegions(root)[0]!["limit"] = 0;
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected test mutation.");
                }
            });

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeasureAsyncRejectsUnknownJsonMembers()
    {
        var fixture = await CreateValidFixtureAsync();
        await RewriteSpecAsync(fixture.SpecPath, root => root["unknown"] = true);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains("Could not read measurement specification", exception.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task MeasureAsyncRejectsEmptySpecification()
    {
        var fixture = await CreateValidFixtureAsync();
        await File.WriteAllTextAsync(fixture.SpecPath, "null", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Equal("The measurement specification is empty.", exception.Message);
    }

    [Fact]
    public async Task MeasureAsyncPropagatesRevisionVerificationFailure()
    {
        var fixture = await CreateValidFixtureAsync();
        var verifier = new RecordingRevisionVerifier(
            new AdoptionMeasurementException("commit mismatch"));

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                verifier,
                CancellationToken.None));

        Assert.Equal("commit mismatch", exception.Message);
    }

    [Fact]
    public async Task GitVerifierAcceptsPinnedCleanFilesAndRejectsMismatchAndDrift()
    {
        var repository = Path.Combine(_root, "git-consumer");
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init");
        await RunGitAsync(repository, "config", "user.name", "AppSurface Test");
        await RunGitAsync(repository, "config", "user.email", "appsurface@example.invalid");
        await File.WriteAllTextAsync(
            Path.Combine(repository, "selected.cs"),
            "baseline",
            CancellationToken.None);
        await RunGitAsync(repository, "add", "selected.cs");
        await RunGitAsync(repository, "commit", "-m", "baseline");
        var commit = (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();

        await GitConsumerRevisionVerifier.Instance.VerifyAsync(
            repository,
            commit,
            ["selected.cs"],
            CancellationToken.None);

        var mismatch = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => GitConsumerRevisionVerifier.Instance.VerifyAsync(
                repository,
                Commit,
                ["selected.cs"],
                CancellationToken.None));
        Assert.Contains("expected baseline commit", mismatch.Message, StringComparison.Ordinal);

        await File.AppendAllTextAsync(
            Path.Combine(repository, "selected.cs"),
            "\ndrift",
            CancellationToken.None);
        var drift = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => GitConsumerRevisionVerifier.Instance.VerifyAsync(
                repository,
                commit,
                ["selected.cs"],
                CancellationToken.None));
        Assert.Contains("differs from pinned commit", drift.Message, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            Path.Combine(repository, "untracked.cs"),
            "not pinned",
            CancellationToken.None);
        var untracked = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => GitConsumerRevisionVerifier.Instance.VerifyAsync(
                repository,
                commit,
                ["untracked.cs"],
                CancellationToken.None));
        Assert.Contains("is not present at pinned commit", untracked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitVerifierRejectsMissingConsumerRoot()
    {
        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => GitConsumerRevisionVerifier.Instance.VerifyAsync(
                Path.Combine(_root, "missing-consumer"),
                Commit,
                ["selected.cs"],
                CancellationToken.None));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandOptionsRejectIncompleteOrUnknownArguments()
    {
        var cases = new (string[] Arguments, string Expected)[]
        {
            ([], "--spec"),
            (["--wat"], "Unknown option"),
            (["--spec"], "requires a value"),
            (["--spec", "spec.json"], "--consumer-root"),
            (["--spec", "spec.json", "--consumer-root", "consumer"], "--output"),
        };

        foreach (var (arguments, expected) in cases)
        {
            var exception = Assert.Throws<AdoptionMeasurementException>(
                () => AdoptionMetricsCommandOptions.Parse(arguments, _root));

            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProgramShowsHelpAndRejectsMissingArguments()
    {
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var helpExitCode = await Program.RunAsync(
            ["--help"],
            standardOut,
            standardError,
            _root,
            CancellationToken.None);
        var missingExitCode = await Program.RunAsync(
            [],
            standardOut,
            standardError,
            _root,
            CancellationToken.None);

        Assert.Equal(0, helpExitCode);
        Assert.Equal(1, missingExitCode);
        Assert.Contains("Usage:", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramReportsInvalidOptions()
    {
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["--wat"],
            standardOut,
            standardError,
            _root,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(standardOut.ToString());
        Assert.Contains("Adoption measurement failed: Unknown option", standardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramMeasuresPinnedConsumerAndWritesResult()
    {
        var fixture = await CreateValidFixtureAsync();
        var commit = await InitializeConsumerRepositoryAsync(fixture.ConsumerRoot);
        await RewriteSpecAsync(fixture.SpecPath, root => root["baselineCommit"] = commit);
        var output = Path.Combine(_root, "program-result.json");
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--spec",
                fixture.SpecPath,
                "--consumer-root",
                fixture.ConsumerRoot,
                "--output",
                output,
            ],
            standardOut,
            standardError,
            fixture.RepositoryRoot,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.Contains($"consumer commit {commit}", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Contains("overallPassed=true", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task ProgramReturnsFailureWhenProposedGuardrailDoesNotPass()
    {
        var fixture = await CreateValidFixtureAsync();
        var commit = await InitializeConsumerRepositoryAsync(fixture.ConsumerRoot);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RepositoryRoot, "repository-registration.cs"),
            "start\none\ntwo\nthree\nend\n",
            CancellationToken.None);
        await RewriteSpecAsync(
            fixture.SpecPath,
            root =>
            {
                root["baselineCommit"] = commit;
                GetRegions(root)[5]!["lineCount"] = 3;
                GetRegions(root)[5]!["passed"] = false;
                root["overallPassed"] = false;
            });
        var output = Path.Combine(_root, "failed-result.json");
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "--spec",
                fixture.SpecPath,
                "--consumer-root",
                fixture.ConsumerRoot,
                "--output",
                output,
            ],
            standardOut,
            standardError,
            fixture.RepositoryRoot,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(output));
        Assert.Contains("overallPassed=false", standardOut.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertRegion(
        AdoptionMeasurementRegionResult region,
        string name,
        AdoptionVariant variant,
        int expectedCount)
    {
        Assert.Equal(name, region.Name);
        Assert.Equal(variant, region.Variant);
        Assert.Equal(expectedCount, region.LineCount);
        Assert.True(region.Passed);
    }

    private async Task<Fixture> CreateValidFixtureAsync(bool reverseRegions = false)
    {
        var consumerRoot = Path.Combine(_root, "consumer");
        var repositoryRoot = Path.Combine(_root, "repository");
        Directory.CreateDirectory(consumerRoot);
        Directory.CreateDirectory(repositoryRoot);

        var names = new[]
        {
            "external-activation-mapping",
            "primary-lifecycle-test",
            "registration",
        };
        var regions = new List<object>();
        foreach (var name in names)
        {
            var consumerPath = $"consumer-{name}.cs";
            var repositoryPath = $"repository-{name}.cs";
            await File.WriteAllTextAsync(
                Path.Combine(consumerRoot, consumerPath),
                "start\r\none\r\n\r\ntwo\r\nend\r\n",
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(repositoryRoot, repositoryPath),
                "start\none\n\n two \nend\n",
                CancellationToken.None);
            regions.Add(CreateRegion("baseline", name, "consumer", consumerPath));
            regions.Add(CreateRegion("proposed", name, "repository", repositoryPath));
        }

        if (reverseRegions)
        {
            regions.Reverse();
        }

        var specPath = Path.Combine(_root, "spec.json");
        await File.WriteAllTextAsync(
            specPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    consumerRepository = "https://example.invalid/consumer",
                    baselineCommit = Commit,
                    regions,
                    overallPassed = true,
                }),
            CancellationToken.None);
        return new(specPath, consumerRoot, repositoryRoot);
    }

    private static object CreateRegion(
        string variant,
        string name,
        string sourceRoot,
        string relativePath)
    {
        return new
        {
            variant,
            name,
            sourceRoot,
            relativePath,
            startToken = "start",
            endToken = "end",
            lineCount = 2,
            limit = 2,
            passed = true,
        };
    }

    private static async Task RewriteSpecAsync(
        string specPath,
        Action<JsonObject> rewrite)
    {
        var root = JsonNode.Parse(
                await File.ReadAllTextAsync(specPath, CancellationToken.None))!
            .AsObject();
        rewrite(root);
        await File.WriteAllTextAsync(
            specPath,
            root.ToJsonString(),
            CancellationToken.None);
    }

    private static JsonArray GetRegions(JsonObject root)
    {
        return root["regions"]!.AsArray();
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static async Task<string> InitializeConsumerRepositoryAsync(string consumerRoot)
    {
        await RunGitAsync(consumerRoot, "init");
        await RunGitAsync(consumerRoot, "config", "user.name", "AppSurface Test");
        await RunGitAsync(consumerRoot, "config", "user.email", "appsurface@example.invalid");
        await RunGitAsync(consumerRoot, "add", ".");
        await RunGitAsync(consumerRoot, "commit", "-m", "baseline");
        return (await RunGitAsync(consumerRoot, "rev-parse", "HEAD")).Trim();
    }

    private sealed record Fixture(
        string SpecPath,
        string ConsumerRoot,
        string RepositoryRoot);

    private sealed class RecordingRevisionVerifier(Exception? exception = null) : IConsumerRevisionVerifier
    {
        public string? ConsumerRoot { get; private set; }

        public string? ExpectedCommit { get; private set; }

        public IReadOnlyList<string>? SelectedRelativePaths { get; private set; }

        public Task VerifyAsync(
            string consumerRoot,
            string expectedCommit,
            IReadOnlyList<string> selectedRelativePaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsumerRoot = consumerRoot;
            ExpectedCommit = expectedCommit;
            SelectedRelativePaths = selectedRelativePaths;
            return exception is null ? Task.CompletedTask : Task.FromException(exception);
        }
    }
}
