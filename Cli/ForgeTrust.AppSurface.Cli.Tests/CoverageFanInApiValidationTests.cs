using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInApiValidationTests
{
    [Fact]
    public void EveryPublicOperationRejectsNullBeforeReturningWork()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.CollectAndCaptureAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.CaptureAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.ComposeAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.AcceptAsync(null!));
        Assert.Throws<ArgumentNullException>(() => CoverageFanIn.VerifyAsync(null!));
    }

    [Fact]
    public void UndefinedModesAndMissingCallerCollectionsFailBeforeFilesystemEffects()
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy();
        var output = Path.Join(temp.Path, "output");
        Assert.Throws<ArgumentException>(() => CoverageFanIn.CaptureAsync(new(policy, temp.Path, output,
            "primary", OutputMode: (CoverageOutputMode)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", [], output,
            OutputMode: (CoverageOutputMode)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", [], output,
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", [], "composed", output,
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", [], "composed", "accepted",
            EvaluationIntent: (CoverageEvaluationIntent)42)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.ComposeAsync(new(policy, temp.Path, "primary", null!, output)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", null!, "composed", output)));
        Assert.Throws<ArgumentException>(() => CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", null!, "composed", "accepted")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void UnsupportedFanInContractFailsThroughThePublicFacadeBeforeFilesystemEffects()
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy() with { ContractVersion = 4 };
        var output = Path.Join(temp.Path, "output");

        Assert.Throws<ArgumentException>(() => CoverageFanIn.CaptureAsync(new(policy, temp.Path, output, "primary")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void V3PatchPolicyRequiresFullObjectFormatBaseBeforeFilesystemEffects()
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: "main")
        };
        var output = Path.Join(temp.Path, "output");

        Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path,
            "primary", [], "composed", output)
        { ExpectedPatchSha256 = new string('a', 64) }));

        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void V3PatchRequiresExpectedCanonicalHashBeforeWork()
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: new string('c', 40))
        };
        var error = Assert.Throws<ArgumentException>(() => CoverageFanIn.AcceptAsync(new(policy, temp.Path,
            "primary", [], "composed", Path.Join(temp.Path, "output"))));
        Assert.Contains("expectedPatchSha256", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task PreCancelledOperationsReturnTypedResultsAndLeaveCallerWritersUsable()
    {
        using var temp = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var progress = new StringWriter();
        using var diagnostics = new StringWriter();
        var policy = CoverageFanInPolicyTests.Policy();
        var result = await CoverageFanIn.CaptureAsync(new(policy, temp.Path, Path.Join(temp.Path, "output"),
            "primary", "coverage.xml", "timings.json"), cancellation.Token, progress, diagnostics);
        Assert.Equal(CoverageOperationOutcome.Cancelled, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.True(result.Effects.CleanupComplete);
        progress.Write("caller retains progress");
        diagnostics.Write("caller retains diagnostics");
        Assert.Contains("caller retains progress", progress.ToString());
        Assert.Contains("caller retains diagnostics", diagnostics.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task V3EnginePreCancelledIdentityProbeReturnsContextualCancellation()
    {
        using var temp = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = ExpectedV3();
        var runner = new IdentityRunner((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new CoverageRunProcessResult(0, string.Empty));
        });
        var request = new CoverageFanInEngineRequest(expected, temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), cancellation.Token);

        Assert.Equal("Cancelled", result.Outcome);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.True(result.CleanupComplete);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV306", diagnostic.Code);
        Assert.Equal("cancelled", diagnostic.Cause);
        Assert.Equal("3", diagnostic.Context!["contractVersion"]);
        Assert.Equal("appsurface-git-patch-v1", diagnostic.Context["diffPolicyId"]);
        Assert.Equal("coverlet-collector-cobertura-v1", diagnostic.Context["reportProfileId"]);
        Assert.Equal(new string('c', 40), diagnostic.Context["baseCommit"]);
        Assert.Equal(new string('a', 40), diagnostic.Context["headCommit"]);
        Assert.Null(diagnostic.Trace);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task V3EngineFailureIncludesPinnedContextAndOnlySanitizedRealExceptionFrames()
    {
        using var temp = TestDirectory.Create();
        var expected = ExpectedV3();
        var secret = $"private failure detail {temp.Path}/credential.txt";
        var runner = new IdentityRunner((_, _) => throw new InvalidOperationException(secret));
        var request = new CoverageFanInEngineRequest(expected, temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("unexpected-failure", diagnostic.Cause);
        Assert.Equal("InvalidOperationException", diagnostic.Context!["exceptionType"]);
        Assert.Equal("3", diagnostic.Context["contractVersion"]);
        Assert.Equal("appsurface-git-patch-v1", diagnostic.Context["diffPolicyId"]);
        Assert.Equal("coverlet-collector-cobertura-v1", diagnostic.Context["reportProfileId"]);
        Assert.Equal(new string('c', 40), diagnostic.Context["baseCommit"]);
        Assert.Equal(new string('a', 40), diagnostic.Context["headCommit"]);
        var trace = Assert.IsAssignableFrom<IReadOnlyList<string>>(diagnostic.Trace);
        Assert.NotEmpty(trace);
        Assert.Contains(trace, frame =>
            frame.StartsWith("ForgeTrust.AppSurface.Cli.Tests.", StringComparison.Ordinal));
        Assert.DoesNotContain(secret, string.Join('\n', trace), StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, string.Join('\n', trace), StringComparison.Ordinal);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("argument", "Rejected", "ASCOV301", "invalid-policy")]
    [InlineData("invalid-data", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("xml", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("format", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("overflow", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("json", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("missing-key", "Rejected", "ASCOV302", "report-invalid")]
    [InlineData("unsupported-git", "Rejected", "ASCOV302", "unsupported-git-version")]
    [InlineData("child-timeout", "TimedOut", "ASCOV306", "deadline-exceeded")]
    public async Task V3EngineMapsTypedIdentityFailuresToTheirStableDiagnosticClassification(
        string failureKind,
        string expectedOutcome,
        string expectedCode,
        string expectedCause)
    {
        using var temp = TestDirectory.Create();
        Exception? exception = failureKind switch
        {
            "argument" => new ArgumentException("invalid identity request"),
            "invalid-data" => new InvalidDataException("invalid tracked source index"),
            "xml" => new System.Xml.XmlException("invalid identity XML"),
            "format" => new FormatException("invalid identity format"),
            "overflow" => new OverflowException("invalid identity range"),
            "json" => new System.Text.Json.JsonException("invalid identity JSON"),
            "missing-key" => new KeyNotFoundException("missing identity key"),
            "unsupported-git" => null,
            "child-timeout" => new CoverageExecutionException("identity command timed out", 124),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        var runner = new IdentityRunner((_, _) =>
        {
            if (failureKind == "unsupported-git")
                return Task.FromResult(new CoverageRunProcessResult(0, "git version 2.45.0\n"));
            throw exception!;
        });
        var request = new CoverageFanInEngineRequest(ExpectedV3(), temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(expectedCause, diagnostic.Cause);
        Assert.Equal("3", diagnostic.Context!["contractVersion"]);
        Assert.Equal(1, runner.Calls);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task V3EngineDistinguishesIdentityCancellationFromCallerCancellation()
    {
        using var temp = TestDirectory.Create();
        var runner = new IdentityRunner((_, _) => throw new OperationCanceledException("identity probe cancelled"));
        var request = new CoverageFanInEngineRequest(ExpectedV3(), temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("TimedOut", result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV306", diagnostic.Code);
        Assert.Equal("deadline-exceeded", diagnostic.Cause);
        Assert.Equal("capture", diagnostic.Stage);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task V3EngineIdentifiesDirtyCheckoutWithoutDisclosingPaths()
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, ".git"));
        const string changedPath = "private-source-name.cs";
        var runner = new IdentityRunner((command, _) =>
        {
            var response = command.Arguments.Contains("status")
                ? new CoverageRunProcessResult(0, $" M {changedPath}\n", StandardOutput: $" M {changedPath}\n")
                : IdentityResult(command, temp.Path, index: string.Empty);
            return Task.FromResult(response);
        });
        var output = Path.Join(temp.Path, "output");
        var request = new CoverageFanInEngineRequest(ExpectedV3(), temp.Path, output,
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("identity-mismatch", diagnostic.Cause);
        Assert.Equal("source", diagnostic.Field);
        Assert.Contains("clean checkout", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.DoesNotContain(changedPath, diagnostic.Recovery, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task V3EngineClassifiesPortableTrackedIndexAliasesAsInvalidEvidence()
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, ".git"));
        await File.WriteAllTextAsync(Path.Join(temp.Path, "First.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Join(temp.Path, "Second.csproj"), "<Project />");
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            TestProjects = ["First.csproj", "Second.csproj"],
        };
        var frozen = CoveragePolicyEncoding.Freeze(policy with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: new string('c', 40)),
        });
        var expected = new CoverageFanInExpected(
            CoveragePolicyEncoding.ToElement(frozen),
            CoveragePolicyEncoding.Digest(frozen));
        var aliasIndex = string.Concat(
            "H 100644 0123456789abcdef0123456789abcdef01234567 0\tFirst.csproj\0",
            "H 100644 0123456789abcdef0123456789abcdef01234567 0\tfirst.csproj\0",
            "H 100644 0123456789abcdef0123456789abcdef01234567 0\tSecond.csproj\0");
        var runner = new IdentityRunner((command, _) => Task.FromResult(IdentityResult(command, temp.Path, aliasIndex)));
        var request = new CoverageFanInEngineRequest(expected, temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("report-invalid", diagnostic.Cause);
        Assert.Equal("source", diagnostic.Field);
        Assert.Contains("portable case/Unicode alias", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.False(Directory.Exists(Path.Join(temp.Path, "output")));
    }

    [Theory]
    [InlineData("aux.md")]
    [InlineData("private-name \n.md")]
    [InlineData("submodule/name\u200B.md")]
    public async Task V3EngineIdentifiesUnsupportedSourceInventoryWithoutDisclosingTheTrackedName(string trackedPath)
    {
        using var temp = TestDirectory.Create();
        Directory.CreateDirectory(Path.Join(temp.Path, ".git"));
        var index = $"H 100644 0123456789abcdef0123456789abcdef01234567 0\t{trackedPath}\0";
        var runner = new IdentityRunner((command, _) => Task.FromResult(IdentityResult(command, temp.Path, index)));
        var request = new CoverageFanInEngineRequest(ExpectedV3(), temp.Path, Path.Join(temp.Path, "output"),
            CoveragePath: Path.Join(temp.Path, "coverage.xml"), TimingsPath: Path.Join(temp.Path, "timings.json"))
        { IdentityRunner = runner };

        var result = await CoverageFanInEngine.ExecuteAsync("capture", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("unsupported-diff", diagnostic.Cause);
        Assert.Equal("source", diagnostic.Field);
        Assert.Contains("tracked-file inventory", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.Contains("outside the patch", diagnostic.Recovery, StringComparison.Ordinal);
        Assert.DoesNotContain(trackedPath, diagnostic.Recovery, StringComparison.Ordinal);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.False(Directory.Exists(Path.Join(temp.Path, "output")));
    }

    [Theory]
    [InlineData("compose-inventory")]
    [InlineData("accept-composition")]
    [InlineData("verify-accepted")]
    [InlineData("collect-deadline")]
    public async Task EngineRejectsIncompleteOperationContractsBeforeFilesystemEffects(string scenario)
    {
        using var temp = TestDirectory.Create();
        var expected = Expected();
        var output = Path.Join(temp.Path, "output");
        var request = new CoverageFanInEngineRequest(expected, temp.Path, output,
            PrimaryCaptureDirectory: Path.Join(temp.Path, "primary"),
            SupplementaryCaptureDirectories: []);
        var operation = scenario switch
        {
            "compose-inventory" => "compose",
            "accept-composition" => "accept",
            "verify-accepted" => "verify",
            "collect-deadline" => "collect",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        if (scenario == "accept-composition")
        {
            request = request with { ComposedOutputDirectory = null };
        }
        else if (scenario == "compose-inventory")
        {
            request = request with { SupplementaryCaptureDirectories = null };
        }
        else if (scenario == "verify-accepted")
        {
            request = request with { ComposedOutputDirectory = Path.Join(temp.Path, "composed") };
        }
        else if (scenario == "collect-deadline")
        {
            request = request with { DeadlineSeconds = 0, CollectionDirectory = Path.Join(temp.Path, "collection") };
        }

        var result = await CoverageFanInEngine.ExecuteAsync(operation, request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task EngineReportsUnknownOperationAfterConfirmingTrustedSourceIdentity()
    {
        using var temp = TestDirectory.Create();
        var project = Path.Join(temp.Path, "tests", "A.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        await File.WriteAllTextAsync(project, "<Project />");
        var source = CoverageFanInPolicyTests.Policy().Source;
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            TestProjects = ["tests/A.Tests.csproj"]
        };
        var frozen = CoveragePolicyEncoding.Freeze(policy);
        var expected = new CoverageFanInExpected(
            CoveragePolicyEncoding.ToElement(frozen),
            CoveragePolicyEncoding.Digest(frozen));
        var request = new CoverageFanInEngineRequest(expected, temp.Path, Path.Join(temp.Path, "output"))
        {
            IdentityRunner = new IdentityRunner((command, _) => Task.FromResult(new CoverageRunProcessResult(
                0,
                command.FileName == "dotnet" ? source.Sdk : string.Join(" ", command.Arguments) switch
                {
                    "--no-replace-objects -c core.fsmonitor=false rev-parse --show-toplevel" => temp.Path,
                    "--no-replace-objects -c core.fsmonitor=false rev-parse HEAD" => source.Commit,
                    "--no-replace-objects -c core.fsmonitor=false rev-parse HEAD^{tree}" => source.Tree,
                    "--no-replace-objects -c core.fsmonitor=false ls-files --stage -v -z" => "H 100644 0123456789abcdef0123456789abcdef01234567 0\ttests/A.Tests.csproj\0",
                    "--no-replace-objects -c core.fsmonitor=false status --porcelain --untracked-files=all --ignore-submodules=none" => string.Empty,
                    _ => throw new InvalidOperationException("Unexpected source identity command.")
                })))
        };

        var result = await CoverageFanInEngine.ExecuteAsync("unrecognized", request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None);

        Assert.Equal("Rejected", result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.Contains("unrecognized", Assert.Single(result.Diagnostics).Field);
        Assert.False(result.Created);
        Assert.False(result.Committed);
        Assert.Equal(6, ((IdentityRunner)request.IdentityRunner).Calls);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }

    [Theory]
    [InlineData("capture", "repository")]
    [InlineData("capture", "output")]
    [InlineData("capture", "coverage")]
    [InlineData("capture", "timings")]
    [InlineData("capture", "raw")]
    [InlineData("capture", "producer")]
    [InlineData("import", "coverage")]
    [InlineData("import", "timings")]
    [InlineData("import", "raw")]
    [InlineData("import", "source-root")]
    [InlineData("compose", "primary")]
    [InlineData("compose", "supplements")]
    [InlineData("accept", "composition")]
    [InlineData("verify", "composition")]
    [InlineData("verify", "accepted")]
    [InlineData("collect", "deadline-small")]
    [InlineData("collect", "deadline-large")]
    [InlineData("collect", "parallelism")]
    [InlineData("collect", "collection")]
    public async Task TypedPublicOperationsRejectOneIncompleteSelectorBeforeSourceAccess(string operation, string selector)
    {
        using var temp = TestDirectory.Create();
        var policy = CoverageFanInPolicyTests.Policy();
        var output = Path.Join(temp.Path, "output");
        ValueTask<CoverageFanInResult> pending;
        if (operation is "capture" or "import")
        {
            var request = new CoverageCaptureRequest(policy, temp.Path, output, "primary", "coverage", "timings");
            if (operation == "import") request = request with { CoveragePath = null, TimingsPath = null, ImportDirectory = "portable", OriginalSourceRoot = "/producer" };
            request = selector switch
            {
                "repository" => request with { RepositoryRoot = " " },
                "output" => request with { OutputDirectory = " " },
                "coverage" => request with { CoveragePath = operation == "import" ? "conflict" : " " },
                "timings" => request with { TimingsPath = operation == "import" ? "conflict" : null },
                "raw" => request with { RawCoveragePath = "conflict" },
                "producer" => request with { ProducerId = "unknown" },
                _ => request with { OriginalSourceRoot = " " }
            };
            pending = CoverageFanIn.CaptureAsync(request);
        }
        else if (operation == "compose")
            pending = CoverageFanIn.ComposeAsync(new(policy, temp.Path, selector == "primary" ? " " : "primary",
                selector == "supplements" ? ["undeclared"] : [], output));
        else if (operation == "accept")
            pending = CoverageFanIn.AcceptAsync(new(policy, temp.Path, "primary", [], " ", output));
        else if (operation == "verify")
            pending = CoverageFanIn.VerifyAsync(new(policy, temp.Path, "primary", [],
                selector == "composition" ? " " : "composition", selector == "accepted" ? " " : "accepted"));
        else
            pending = CoverageFanIn.CollectAndCaptureAsync(new(policy, temp.Path, output,
                selector == "collection" ? " " : "collection", "primary",
                selector == "deadline-small" ? 0 : selector == "deadline-large" ? 86401 : 60,
                Parallelism: selector == "parallelism" ? 0 : 1));
        var result = await pending;
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.Equal("ASCOV301", Assert.Single(result.Diagnostics).Code);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    private static CoverageRunProcessResult IdentityResult(CoverageRunProcessRequest command, string root, string index)
    {
        var source = CoverageFanInPolicyTests.Policy().Source;
        var args = command.Arguments;
        var output = command.FileName == "dotnet" ? source.Sdk
            : args.Contains("--version") ? "git version 2.46.0"
            : args.Contains("config") ? string.Empty
            : args.Contains("--is-shallow-repository") ? "false"
            : args.Contains("--git-common-dir") ? ".git"
            : args.Contains("--show-toplevel") ? root
            : args.Contains("HEAD^{tree}") ? source.Tree
            : args.Contains("HEAD") ? source.Commit
            : args.Contains("status") ? string.Empty
            : args.Contains("ls-files") ? index
            : throw new InvalidOperationException("Unexpected identity fixture command.");
        return new(args.Contains("config") ? 1 : 0, output, StandardOutput: output);
    }

    private static CoverageFanInExpected Expected()
    {
        var policy = CoveragePolicyEncoding.Freeze(CoverageFanInPolicyTests.Policy());
        return new(CoveragePolicyEncoding.ToElement(policy),
            CoveragePolicyEncoding.Digest(policy));
    }

    private static CoverageFanInExpected ExpectedV3()
    {
        var policy = CoverageFanInPolicyTests.Policy() with
        {
            ContractVersion = 3,
            ReportProfileId = "coverlet-collector-cobertura-v1",
            Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: new string('c', 40))
        };
        var frozen = CoveragePolicyEncoding.Freeze(policy);
        return new(CoveragePolicyEncoding.ToElement(frozen), CoveragePolicyEncoding.Digest(frozen));
    }

    private sealed class IdentityRunner(
        Func<CoverageRunProcessRequest, CancellationToken, Task<CoverageRunProcessResult>> execute)
        : ICoverageRunProcessRunner
    {
        internal int Calls { get; private set; }

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return execute(request, cancellationToken);
        }
    }
}
