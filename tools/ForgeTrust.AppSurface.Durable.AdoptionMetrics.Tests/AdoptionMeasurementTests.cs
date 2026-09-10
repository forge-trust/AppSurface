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
    public async Task MainRoutesHelpAndCommandFailuresThroughTheConsoleBoundary()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();
        try
        {
            Console.SetOut(standardOut);
            Console.SetError(standardError);

            Assert.Equal(0, await Program.Main(["--help"]));
            Assert.Equal(1, await Program.Main(["--unknown"]));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains(
            "ForgeTrust.AppSurface.Durable.AdoptionMetrics",
            standardOut.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "Adoption measurement failed: Unknown option '--unknown'.",
            standardError.ToString(),
            StringComparison.Ordinal);
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

    [Fact]
    public async Task MeasureAsyncRejectsSymbolicLinksInsideADeclaredSourceRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Symbolic-link path validation runs only on Unix hosts.");
        }

        var fixture = await CreateValidFixtureAsync();
        var selectedPath = Path.Combine(fixture.RepositoryRoot, "repository-registration.cs");
        var outsidePath = Path.Combine(_root, "outside.cs");
        await File.WriteAllTextAsync(outsidePath, "start\none\ntwo\nend\n", CancellationToken.None);
        File.Delete(selectedPath);
        File.CreateSymbolicLink(selectedPath, outsidePath);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains("must not contain symbolic links", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeasureAsyncRejectsAnUnsupportedSourceRoot()
    {
        var fixture = await CreateValidFixtureAsync();
        await RewriteSpecAsync(
            fixture.SpecPath,
            root => GetRegions(root)[5]!["sourceRoot"] = 999);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.MeasureAsync(
                fixture.SpecPath,
                fixture.ConsumerRoot,
                fixture.RepositoryRoot,
                new RecordingRevisionVerifier(),
                CancellationToken.None));

        Assert.Contains("unsupported source root '999'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MeasureAsyncRejectsASymbolicLinkUsedAsTheSourceRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Symbolic-link path validation runs only on Unix hosts.");
        }

        var fixture = await CreateValidFixtureAsync();
        var linkedRoot = fixture.ConsumerRoot;
        var targetRoot = linkedRoot + "-target";
        Directory.Move(linkedRoot, targetRoot);
        Directory.CreateSymbolicLink(linkedRoot, targetRoot);
        try
        {
            var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
                () => AdoptionMeasurementEngine.MeasureAsync(
                    fixture.SpecPath,
                    linkedRoot,
                    fixture.RepositoryRoot,
                    new RecordingRevisionVerifier(),
                    CancellationToken.None));

            Assert.Contains("source path must not contain symbolic links", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedRoot);
            Directory.Move(targetRoot, linkedRoot);
        }
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
    public async Task MeasureAsyncReportsAnUnreadableRegionFile()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Unix file permissions provide the deterministic unreadable-file fixture.");
        }

        var fixture = await CreateValidFixtureAsync();
        var path = Path.Combine(fixture.RepositoryRoot, "repository-registration.cs");
        var originalMode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserWrite);
        try
        {
            Exception? readException = null;
            try
            {
                await File.ReadAllTextAsync(path, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                readException = exception;
            }

            if (readException is null)
            {
                throw Xunit.Sdk.SkipException.ForSkip(
                    "The test process can read permission-restricted files on this host.");
            }

            var measurementException = await Assert.ThrowsAsync<AdoptionMeasurementException>(
                () => AdoptionMeasurementEngine.MeasureAsync(
                    fixture.SpecPath,
                    fixture.ConsumerRoot,
                    fixture.RepositoryRoot,
                    new RecordingRevisionVerifier(),
                    CancellationToken.None));

            Assert.Contains("Could not read region 'registration' source file", measurementException.Message, StringComparison.Ordinal);
            Assert.Same(readException.GetType(), measurementException.InnerException?.GetType());
        }
        finally
        {
            File.SetUnixFileMode(path, originalMode);
        }
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
    public async Task GitVerifierBoundsCommandsAndPreservesCallerCancellation()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The deterministic Git-process deadline seam uses a Unix shell fixture.");
        }

        var executable = await CreateUnixExecutableAsync(
            Path.Combine(_root, "blocking-git"),
            "#!/bin/sh\nsleep 30\n");

        var timeoutVerifier = new GitConsumerRevisionVerifier(
            executable,
            TimeSpan.FromSeconds(1));
        var timeout = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => timeoutVerifier.VerifyAsync(
                _root,
                Commit,
                [],
                CancellationToken.None));
        Assert.Contains("within 1 seconds", timeout.Message, StringComparison.Ordinal);

        var cancellationVerifier = new GitConsumerRevisionVerifier(
            executable,
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancellationVerifier.VerifyAsync(
                _root,
                Commit,
                [],
                cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public void GitVerifierRejectsInvalidProcessConfiguration()
    {
        Assert.Throws<ArgumentException>(
            () => new GitConsumerRevisionVerifier(" ", TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GitConsumerRevisionVerifier("git", TimeSpan.Zero));
    }

    [Fact]
    public async Task GitVerifierReportsWhenGitCannotStart()
    {
        var verifier = new GitConsumerRevisionVerifier(
            Path.Combine(_root, "missing-git"),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => verifier.VerifyAsync(
                _root,
                Commit,
                [],
                CancellationToken.None));

        Assert.Equal("Could not start Git to verify the consumer checkout.", exception.Message);
        Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);
    }

    [Fact]
    public async Task GitVerifierReportsGitFailureOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The deterministic Git-process fixture uses a Unix shell script.");
        }

        var executable = await CreateUnixExecutableAsync(
            Path.Combine(_root, "failing-git"),
            "#!/bin/sh\nprintf 'fatal: fixture failure\\n' >&2\nexit 1\n");
        var verifier = new GitConsumerRevisionVerifier(executable, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => verifier.VerifyAsync(
                _root,
                Commit,
                [],
                CancellationToken.None));

        Assert.Contains("Git could not verify the consumer checkout: fatal: fixture failure", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitVerifierBoundsSourceVerificationAndPreservesCallerCancellation()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The deterministic Git-process deadline seam uses a Unix shell fixture.");
        }

        var executable = await CreateUnixExecutableAsync(
            Path.Combine(_root, "blocking-source-git"),
            $"#!/bin/sh\nif [ \"$1\" = \"rev-parse\" ]; then\n  printf '%s\\n' '{Commit}'\nelse\n  sleep 30\nfi\n");

        var timeoutVerifier = new GitConsumerRevisionVerifier(
            executable,
            TimeSpan.FromSeconds(1));
        var timeout = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => timeoutVerifier.VerifyAsync(
                _root,
                Commit,
                ["selected.cs"],
                CancellationToken.None));
        Assert.Contains("consumer source verification within 1 seconds", timeout.Message, StringComparison.Ordinal);

        var cancellationVerifier = new GitConsumerRevisionVerifier(
            executable,
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancellationVerifier.VerifyAsync(
                _root,
                Commit,
                ["selected.cs"],
                cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public async Task GitVerifierReportsWhenSourceVerificationCannotStart()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The disappearing-working-directory fixture uses Unix process semantics.");
        }

        var consumerRoot = Path.Combine(_root, "disappearing-consumer");
        Directory.CreateDirectory(consumerRoot);
        var executable = await CreateUnixExecutableAsync(
            Path.Combine(_root, "disappearing-git"),
            $"#!/bin/sh\nif [ \"$1\" = \"rev-parse\" ]; then\n  rmdir \"$PWD\"\n  printf '%s\\n' '{Commit}'\nfi\n");

        var verifier = new GitConsumerRevisionVerifier(executable, TimeSpan.FromSeconds(1));
        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => verifier.VerifyAsync(
                consumerRoot,
                Commit,
                ["selected.cs"],
                CancellationToken.None));

        Assert.Equal("Could not start Git to verify consumer source files.", exception.Message);
        Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);
    }

    [Fact]
    public void ToJsonValueRejectsUnsupportedVariants()
    {
        Assert.Equal("baseline", AdoptionMeasurementEngine.ToJsonValue(AdoptionVariant.Baseline));
        Assert.Equal("proposed", AdoptionMeasurementEngine.ToJsonValue(AdoptionVariant.Proposed));

        var exception = Assert.Throws<AdoptionMeasurementException>(
            () => AdoptionMeasurementEngine.ToJsonValue((AdoptionVariant)999));

        Assert.Contains("Unsupported variant '999'", exception.Message, StringComparison.Ordinal);
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
    public async Task ProgramReportsCallerCancellationWithoutAStackTrace()
    {
        var fixture = await CreateValidFixtureAsync();
        using var standardOut = new StringWriter();
        using var standardError = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await Program.RunAsync(
            [
                "--spec",
                fixture.SpecPath,
                "--consumer-root",
                fixture.ConsumerRoot,
                "--output",
                Path.Combine(_root, "result.json"),
            ],
            standardOut,
            standardError,
            _root,
            cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Empty(standardOut.ToString());
        Assert.Equal($"Adoption measurement canceled.{Environment.NewLine}", standardError.ToString());
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
    public async Task ProgramReportsOutputWriteFailuresWithoutEscapingTheCommandBoundary()
    {
        var fixture = await CreateValidFixtureAsync();
        var commit = await InitializeConsumerRepositoryAsync(fixture.ConsumerRoot);
        await RewriteSpecAsync(fixture.SpecPath, root => root["baselineCommit"] = commit);
        var output = Path.Combine(_root, "output-is-a-directory");
        Directory.CreateDirectory(output);
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
        Assert.Empty(standardOut.ToString());
        Assert.Contains(
            "Adoption measurement failed: Could not write adoption measurement output",
            standardError.ToString(),
            StringComparison.Ordinal);
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

    [Fact]
    public async Task WriterRejectsASymbolicLinkWithoutChangingItsTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Symbolic-link output validation runs only on Unix hosts.");
        }

        var fixture = await CreateValidFixtureAsync();
        var result = await AdoptionMeasurementEngine.MeasureAsync(
            fixture.SpecPath,
            fixture.ConsumerRoot,
            fixture.RepositoryRoot,
            new RecordingRevisionVerifier(),
            CancellationToken.None);
        var target = Path.Combine(_root, "writer-target.json");
        var output = Path.Combine(_root, "writer-output.json");
        await File.WriteAllTextAsync(target, "sentinel", CancellationToken.None);
        File.CreateSymbolicLink(output, target);

        var exception = await Assert.ThrowsAsync<AdoptionMeasurementException>(
            () => AdoptionMeasurementWriter.WriteAsync(output, result, CancellationToken.None));

        Assert.Contains("output file must not be a symbolic link", exception.Message, StringComparison.Ordinal);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(target, CancellationToken.None));
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
                TestPathUtils.PathUnder(consumerRoot, consumerPath),
                "start\r\none\r\n\r\ntwo\r\nend\r\n",
                CancellationToken.None);
            await File.WriteAllTextAsync(
                TestPathUtils.PathUnder(repositoryRoot, repositoryPath),
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, error);
        Assert.True(process.ExitCode == 0, error.Result);
        return output.Result;
    }

    private static async Task<string> CreateUnixExecutableAsync(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The executable fixture runs only on Unix hosts.");
        }

        await File.WriteAllTextAsync(path, content, CancellationToken.None);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
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
