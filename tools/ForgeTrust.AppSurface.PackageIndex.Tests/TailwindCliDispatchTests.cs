namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Checks the public command boundary for release proof modes and missing authority.</summary>
public sealed class TailwindCliDispatchTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot, "tailwind-cli-dispatch", Guid.NewGuid().ToString("N"));

    // protects=explicit transport authority and safe mode dispatch; fails_when=general defaults manufacture inputs;
    // no_claim=injected dispatch does not validate actual source, content, HTTP or hosted credentials.
    [Theory]
    [InlineData("--repository")]
    [InlineData("--repository-id")]
    [InlineData("--producer-run-id")]
    [InlineData("--producer-attempt")]
    [InlineData("--source-commit")]
    [InlineData("--producer-artifact-id")]
    [InlineData("--producer-receipt")]
    [InlineData("--expected-subject-sha256")]
    [InlineData("--repo-root")]
    [InlineData("--work-directory")]
    [InlineData("--artifacts-output")]
    [InlineData("--report-directory")]
    public async Task ArtifactTransport_RequiresEachExplicitOption(string missing)
    {
        var args = TransportArguments();
        var index = args.IndexOf(missing);
        args.RemoveRange(index, 2);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var called = false;
        var exit = await Program.RunAsync(args.ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => { called = true; throw new InvalidOperationException(); },
            artifactTransportToken: () => "secret-sentinel");
        Assert.Equal(1, exit);
        Assert.False(called);
        Assert.Contains("artifact-transport failed: artifact-input-invalid", stderr.ToString());
        Assert.Contains(missing, stderr.ToString());
        Assert.DoesNotContain("secret-sentinel", stdout.ToString() + stderr);
    }

    // protects=quoted explicit context forwarding; fails_when=an ID or attempt is derived from the receipt;
    // no_claim=the callback is dispatch-only and cannot prove the adapter's validation behavior.
    [Fact]
    public async Task ArtifactTransport_ForwardsContextAndSafeOutcome()
    {
        TailwindArtifactTransportRequest? captured = null;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (request, _) =>
            {
                captured = request;
                return Task.FromResult(new ArtifactTransportResult(true, "succeeded", null, null, true));
            }, artifactTransportToken: () => "secret-sentinel");
        Assert.Equal(0, exit);
        Assert.NotNull(captured);
        Assert.Equal("987654321", captured.Expected.ArtifactId);
        Assert.Equal("2", captured.Expected.RunAttempt);
        Assert.Equal("forge-trust/AppSurface", captured.Expected.Repository);
        Assert.Equal(Path.Combine(_root, "receipt.json"), captured.ReceiptPath);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.DoesNotContain("secret-sentinel", stdout.ToString());
    }

    // protects=no raw new-mode exception leakage; fails_when=legacy formatting prints caller data;
    // no_claim=dispatch fixtures do not establish report generation or actual adapter fault behavior.
    [Theory]
    [InlineData("--artifacts-input")]
    [InlineData("--producer-subject")]
    [InlineData("--repository")]
    [InlineData("--unknown-secret-sentinel")]
    public async Task ArtifactTransport_RejectsInapplicableDuplicateAndUnknownOptions(string extra)
    {
        var args = TransportArguments(); args.AddRange([extra, "secret-sentinel"]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var exit = await Program.RunAsync(args.ToArray(), stdout, stderr, _root, artifactTransportToken: () => "secret-sentinel");
        Assert.Equal(1, exit);
        Assert.Contains("artifact-input-invalid", stderr.ToString());
        Assert.DoesNotContain("secret-sentinel", stdout.ToString() + stderr);
    }

    // protects=fixed stderr when report output is unavailable; fails_when=failure is hidden or a secret is emitted;
    // no_claim=callback only represents an adapter result; real IO failures have adapter tests.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtifactTransport_HandlesFailureAndUnexpectedExceptionsWithoutRawMessages(bool throws)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => throws
                ? throw new Exception("secret-sentinel")
                : Task.FromResult(new ArtifactTransportResult(false, "failed", "artifact-bytes-digest-mismatch", "secret-sentinel", false)),
            artifactTransportToken: () => "secret-sentinel");
        Assert.Equal(1, exit);
        Assert.Equal(throws ? "artifact-transport failed: artifact-output-failed" : "artifact-transport failed: artifact-bytes-digest-mismatch", stderr.ToString().Trim());
        Assert.DoesNotContain("secret-sentinel", stdout.ToString() + stderr);
    }

    // protects=explicit environment authority and cancellation; fails_when=work runs without credentials or cancellation is swallowed;
    // no_claim=dispatch-only callbacks do not establish remote service behavior.
    [Theory]
    [InlineData("missing-token")]
    [InlineData("cancelled")]
    [InlineData("next-action")]
    public async Task ArtifactTransport_SafeEnvironmentCancellationAndRecovery(string scenario)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var calls = 0;
        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) =>
            {
                calls++;
                if (scenario == "cancelled") throw new OperationCanceledException("secret-sentinel");
                return Task.FromResult(new ArtifactTransportResult(false, "failed", "artifact-attempt-mismatch", "Re-run all jobs with maintainer authorization.", true));
            }, artifactTransportToken: () => scenario == "missing-token" ? null : "secret-sentinel");
        Assert.Equal(1, exit);
        Assert.Equal(scenario == "missing-token" ? 0 : 1, calls);
        Assert.Contains(scenario == "cancelled" ? "artifact-cancelled" : scenario == "missing-token" ? "artifact-input-invalid" : "artifact-attempt-mismatch", stderr.ToString());
        if (scenario == "missing-token") Assert.Contains("GH_TOKEN", stderr.ToString());
        if (scenario == "next-action") Assert.Contains("maintainer authorization", stderr.ToString());
        Assert.DoesNotContain("secret-sentinel", stdout.ToString() + stderr);
    }

    // protects=help availability and mode discovery; fails_when=help demands a token or undocumented options;
    // no_claim=help does not execute artifact verification.
    [Fact]
    public async Task ArtifactTransport_HelpDocumentsRequiredAuthorityWithoutCredentials()
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var exit = await Program.RunAsync(["verify-tailwind-evidence", "--mode", "artifact-transport", "--help"], stdout, stderr, _root);
        Assert.Equal(0, exit);
        Assert.Contains("artifact-transport", stdout.ToString());
        Assert.Contains("--producer-receipt", stdout.ToString());
        Assert.Contains("same volume", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // protects=existing mode option boundaries; fails_when=new transport flags silently change another mode;
    // no_claim=the invocation refuses before old-mode workflow execution.
    [Theory]
    [InlineData("--repository")]
    [InlineData("--producer-receipt")]
    public async Task ArtifactTransport_FlagsRemainExclusiveToTheNewMode(string option)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var exit = await Program.RunAsync(["verify-tailwind-evidence", "--mode", "aggregate", option, "value"], stdout, stderr, _root);
        Assert.Equal(1, exit);
        Assert.Contains("apply only to artifact-transport", stderr.ToString());
    }

    // protects=canonical explicit identity at the command boundary; fails_when=zero/aliased identity reaches a workflow;
    // no_claim=boundary validation does not authenticate the protected receipt.
    [Theory]
    [InlineData("--repository", "forge-trust/..")]
    [InlineData("--repository", "./AppSurface")]
    [InlineData("--repository-id", "0")]
    [InlineData("--producer-run-id", "01")]
    [InlineData("--producer-attempt", "0")]
    [InlineData("--producer-artifact-id", "1e3")]
    [InlineData("--source-commit", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ArtifactTransport_RejectsNoncanonicalExpectedIdentity(string option, string value)
    {
        var args = TransportArguments(); args[args.IndexOf(option) + 1] = value;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var called = false;
        var exit = await Program.RunAsync(args.ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => { called = true; return Task.FromResult(new ArtifactTransportResult(true, "succeeded", null, null, true)); },
            artifactTransportToken: () => "fixture-token");
        Assert.Equal(1, exit);
        Assert.False(called);
        Assert.Contains("artifact-input-invalid", stderr.ToString());
    }

    // protects=environment-only credential syntax and redaction; fails_when=malformed tokens reach HTTP or appear in output;
    // no_claim=synthetic tokens do not prove hosted authentication.
    [Theory]
    [InlineData("bad token-sentinel")]
    [InlineData("bad\r\ntoken-sentinel")]
    public async Task ArtifactTransport_RejectsMalformedEnvironmentTokenBeforeDispatch(string credential)
    {
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var called = false;
        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => { called = true; return Task.FromResult(new ArtifactTransportResult(true, "succeeded", null, null, true)); },
            artifactTransportToken: () => credential);
        Assert.Equal(1, exit);
        Assert.False(called);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("artifact-input-invalid", stderr.ToString());
        Assert.Contains("GH_TOKEN", stderr.ToString());
        Assert.DoesNotContain("token-sentinel", stderr.ToString());
    }

    // protects=malformed option values refuse before dispatch without echoing caller values;
    // fails_when=a missing value, blank value, or duplicate mode can reach artifact transport;
    // why_new=required-option tests remove the option instead of corrupting its supplied value;
    // seam=Program.RunAsync (production caller: Main); no_claim=argument parsing does not prove hosted authority.
    [Theory]
    [InlineData("missing-value")]
    [InlineData("blank-value")]
    [InlineData("option-as-value")]
    [InlineData("duplicate-mode")]
    public async Task ArtifactTransport_RejectsMalformedOptionValuesBeforeDispatch(string scenario)
    {
        var args = TransportArguments();
        if (scenario == "missing-value") args.RemoveAt(args.Count - 1);
        else if (scenario == "blank-value") args[^1] = " ";
        else if (scenario == "option-as-value") args[^1] = "--producer-receipt";
        else args.AddRange(["--mode", "secret-sentinel"]);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var called = false;

        var exit = await Program.RunAsync(args.ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => { called = true; throw new InvalidOperationException(); },
            artifactTransportToken: () => "fixture-token");

        Assert.Equal(1, exit);
        Assert.False(called);
        Assert.Empty(stdout.ToString());
        Assert.Contains("artifact-input-invalid", stderr.ToString());
        Assert.DoesNotContain("secret-sentinel", stderr.ToString());
    }

    // protects=expected local failures at the command boundary retain a fixed safe refusal;
    // fails_when=filesystem/path or state exception text is sent through legacy error formatting;
    // why_new=the existing unexpected-failure case covers only the catch-all branch;
    // seam=Program.RunAsync (production caller: Main); no_claim=injected exceptions do not reproduce a real disk failure.
    [Theory]
    [InlineData("argument")]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("state")]
    public async Task ArtifactTransport_KeepsLocalFailureMessagesSafe(string scenario)
    {
        Exception failure = scenario switch
        {
            "argument" => new ArgumentException("secret-sentinel"),
            "io" => new IOException("secret-sentinel"),
            "access" => new UnauthorizedAccessException("secret-sentinel"),
            _ => new InvalidOperationException("secret-sentinel")
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => throw failure, artifactTransportToken: () => "fixture-token");

        Assert.Equal(1, exit);
        Assert.Empty(stdout.ToString());
        Assert.Equal("artifact-transport failed: artifact-input-invalid", stderr.ToString().Trim());
        Assert.DoesNotContain("secret-sentinel", stderr.ToString());
    }

    // protects=the real default workflow keeps path ownership checks at the CLI boundary;
    // fails_when=production dispatch bypasses receipt-container separation or emits a success marker;
    // no_claim=this local pre-HTTP refusal does not establish GitHub token access or hosted acceptance.
    [Fact]
    public async Task ArtifactTransport_DefaultWorkflowRefusesReceiptContainerOverlapWithoutEligibleOutputs()
    {
        Directory.CreateDirectory(_root);
        var receipt = TestPathUtils.PathUnder(_root, "receipt.json");
        await File.WriteAllTextAsync(receipt, "secret-sentinel");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportToken: () => "fixture-token");

        Assert.Equal(1, exit);
        Assert.Empty(stdout.ToString());
        Assert.Equal("artifact-transport failed: artifact-input-invalid", stderr.ToString().Trim());
        Assert.DoesNotContain("secret-sentinel", stderr.ToString());
        Assert.DoesNotContain("fixture-token", stderr.ToString());
        Assert.Equal("secret-sentinel", await File.ReadAllTextAsync(receipt));
        foreach (var directory in new[] { "work", "bundle", "report" })
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(_root, directory)));
    }

    // protects=fatal allocation failures are propagated instead of presented as handled transport refusals;
    // fails_when=the catch-all masks a fatal runtime exception or emits ordinary recovery output;
    // no_claim=the injected exception does not allocate excessive memory or exercise OS memory exhaustion.
    [Fact]
    public async Task ArtifactTransport_FatalAllocationFailureIsNotReportedAsHandledRefusal()
    {
        var failure = new OutOfMemoryException("fatal-allocation-sentinel");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() => Program.RunAsync(
            TransportArguments().ToArray(), stdout, stderr, _root,
            artifactTransportAsync: (_, _) => throw failure, artifactTransportToken: () => "fixture-token"));

        Assert.Same(failure, observed);
        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
        Assert.False(Directory.Exists(_root));
    }

    private List<string> TransportArguments() =>
    [
        "verify-tailwind-evidence", "--mode", "artifact-transport", "--repo-root", _root,
        "--repository", "forge-trust/AppSurface", "--repository-id", "123456", "--producer-run-id", "123456789",
        "--producer-attempt", "2", "--source-commit", new string('a', 40), "--producer-artifact-id", "987654321",
        "--producer-receipt", "receipt.json", "--expected-subject-sha256", new string('b', 64),
        "--work-directory", "work", "--artifacts-output", "bundle", "--report-directory", "report"
    ];

    [Theory]
    [InlineData("verify-tailwind-consumer", "invalid", "must be local or release")]
    [InlineData("verify-tailwind-evidence", "invalid", "must be producer-binding")]
    [InlineData("verify-tailwind-evidence", "publish-preflight", "--report-directory")]
    [InlineData("verify-tailwind-evidence", "validate-publication-start", "--report-directory")]
    public async Task ReleaseCommands_RejectInvalidOrIncompleteAuthorityAtCliBoundary(
        string command, string mode, string expectedError)
    {
        Directory.CreateDirectory(_root);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            [command, "--repo-root", _root, "--mode", mode], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains(expectedError, stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("succeeded", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggregateCli_LeavesNoSuccessReceiptWhenProducerAuthorityIsMissing()
    {
        Directory.CreateDirectory(_root);
        var report = TestPathUtils.PathUnder(_root, "aggregate-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(
            ["verify-tailwind-evidence", "--repo-root", _root, "--mode", "aggregate",
                "--report-directory", report], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(TestPathUtils.PathUnder(report, "tailwind-native-aggregate.json")));
        Assert.True(stderr.ToString().Length > 0 || stdout.ToString().Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProducerBindingCli_WritesDiagnosticsWhenValidationFails()
    {
        Directory.CreateDirectory(_root);
        var report = TestPathUtils.PathUnder(_root, "binding-report");
        var binding = TestPathUtils.PathUnder(_root, "binding.json");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["verify-tailwind-evidence", "--repo-root", _root, "--mode", "producer-binding",
                "--report-directory", report, "--resolved-binding-output", binding], stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(binding));
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json")));
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "summary.md")));
    }

    [Fact]
    public async Task ReleaseConsumerCli_ReportsProducerBindingFailureWithoutInvokingNativeCommands()
    {
        Directory.CreateDirectory(_root);
        var work = TestPathUtils.PathUnder(_root, "release-work");
        var report = TestPathUtils.PathUnder(_root, "release-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["verify-tailwind-consumer", "--repo-root", _root, "--mode", "release",
                "--work-directory", work, "--report-directory", report],
            stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains("failed", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "diagnostics.json"))
            || File.Exists(TestPathUtils.PathUnder(report, "tailwind-failure-diagnostics.json")));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task LocalConsumerCli_ReportsMissingManifestWithoutReleaseReceipt()
    {
        Directory.CreateDirectory(_root);
        var work = TestPathUtils.PathUnder(_root, "local-work");
        var report = TestPathUtils.PathUnder(_root, "local-report");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["verify-tailwind-consumer", "--repo-root", _root, "--mode", "local",
                "--work-directory", work, "--report-directory", report],
            stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(TestPathUtils.PathUnder(report, "tailwind-native-host-proof.json")));
        Assert.True(stderr.ToString().Length > 0 || stdout.ToString().Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PublishPrereleaseCli_ForwardsTailwindIdentityToInjectedPublisher()
    {
        Directory.CreateDirectory(_root);
        var artifacts = TestPathUtils.PathUnder(_root, "candidate-bundle");
        var manifest = TestPathUtils.PathUnder(_root, "candidate-manifest.json");
        var subject = TestPathUtils.PathUnder(artifacts, "tailwind-proof-subject.json");
        var aggregate = TestPathUtils.PathUnder(_root, "aggregate");
        var publication = TestPathUtils.PathUnder(_root, "prepared");
        var startReceipt = TestPathUtils.PathUnder(_root, "start", "publication-start-receipt.json");
        var report = TestPathUtils.PathUnder(_root, "publish-report");
        const string subjectHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string aggregateHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string sourceCommit = "cccccccccccccccccccccccccccccccccccccccc";
        PackagePublishRequest? dispatched = null;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = await Program.RunAsync(
            ["publish-prerelease", "--repo-root", _root, "--artifacts-input", artifacts,
                "--artifact-manifest", manifest, "--publish-log", TestPathUtils.PathUnder(_root, "publish.md"),
                "--producer-subject", subject, "--producer-artifact-id", "123",
                "--expected-subject-sha256", subjectHash, "--repository-id", "456",
                "--producer-run-id", "789", "--source-commit", sourceCommit,
                "--aggregate-input", aggregate, "--aggregate-artifact-id", "321",
                "--expected-aggregate-sha256", aggregateHash, "--publication-directory", publication,
                "--publication-start-receipt", startReceipt, "--publication-start-artifact-id", "654",
                "--report-directory", report],
            stdout,
            stderr,
            _root,
            publishPrereleaseAsync: (request, _) =>
            {
                dispatched = request;
                return Task.FromResult(new PackagePublishLedger("1.2.3-preview.798", request.Source, []));
            });

        Assert.Equal(0, exit);
        Assert.Empty(stderr.ToString());
        Assert.Contains("Published 0 prerelease package artifacts", stdout.ToString(), StringComparison.Ordinal);
        Assert.NotNull(dispatched?.TailwindEvidence);
        Assert.Equal("123", dispatched!.TailwindEvidence!.ProducerArtifactId);
        Assert.Equal(subjectHash, dispatched.TailwindEvidence.ExpectedSubjectSha256);
        Assert.Equal(aggregate, dispatched.TailwindEvidence.AggregateInputPath);
        Assert.Equal("654", dispatched.TailwindEvidence.PublicationStartArtifactId);
        Assert.Equal(artifacts, dispatched.ArtifactsInputPath);
        Assert.Equal(manifest, dispatched.ArtifactManifestPath);
    }

    [Theory]
    [InlineData("publish-preflight")]
    [InlineData("validate-publication-start")]
    public async Task PublicationEvidenceCli_ResolvesPackagePlanBeforeRejectingUnstampedSource(string mode)
    {
        Directory.CreateDirectory(_root);
        var (manifestPath, artifacts, artifactManifestPath) = await CreatePublicationPlanAsync();
        var report = TestPathUtils.PathUnder(_root, $"{mode}-report");
        var publication = TestPathUtils.PathUnder(_root, $"{mode}-prepared");
        var aggregate = TestPathUtils.PathUnder(_root, "aggregate");
        var startReceipt = TestPathUtils.PathUnder(_root, "uploaded-start", "publication-start-receipt.json");
        var arguments = new List<string>
        {
            "verify-tailwind-evidence", "--repo-root", _root, "--manifest", manifestPath,
            "--artifacts-input", artifacts, "--artifact-manifest", artifactManifestPath,
            "--mode", mode, "--producer-subject", TestPathUtils.PathUnder(artifacts, "tailwind-proof-subject.json"),
            "--producer-artifact-id", "123", "--expected-subject-sha256", new string('a', 64),
            "--repository-id", "456", "--producer-run-id", "789", "--source-commit", new string('c', 40),
            "--aggregate-input", aggregate, "--aggregate-artifact-id", "321",
            "--expected-aggregate-sha256", new string('b', 64), "--publication-directory", publication,
            "--report-directory", report
        };
        if (mode == "validate-publication-start")
            arguments.AddRange(["--publication-start-receipt", startReceipt, "--publication-start-artifact-id", "654"]);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await Program.RunAsync(arguments.ToArray(), stdout, stderr, _root);

        Assert.Equal(1, exit);
        Assert.Contains("Exact-source", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("succeeded", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(publication));
    }

    private async Task<(string ManifestPath, string ArtifactsPath, string ArtifactManifestPath)> CreatePublicationPlanAsync()
    {
        const string webProjectRelativePath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        const string projectRelativePath = "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj";
        var webProjectPath = TestPathUtils.PathUnder(_root, "Web", "ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Web.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(webProjectPath)!);
        await File.WriteAllTextAsync(webProjectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>ForgeTrust.AppSurface.Web</PackageId><IsPackable>true</IsPackable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(_root, "Web", "ForgeTrust.AppSurface.Web", "README.md"), "# Web fixture\n");
        var projectPath = TestPathUtils.PathUnder(_root, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "ForgeTrust.AppSurface.Web.Tailwind.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        await File.WriteAllTextAsync(projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>ForgeTrust.AppSurface.Web.Tailwind</PackageId><IsPackable>true</IsPackable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(_root, "Web", "ForgeTrust.AppSurface.Web.Tailwind", "README.md"), "# Tailwind fixture\n");

        var manifestPath = TestPathUtils.PathUnder(_root, "packages", "package-index.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath,
            $$"""
            packages:
              - project: {{webProjectRelativePath}}
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 1
                use_when: Validate publication command dispatch.
                includes: Web fixture package.
                does_not_include: Other packages.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: {{projectRelativePath}}
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Validate publication command dispatch.
                includes: Tailwind fixture package.
                does_not_include: Other packages.
                start_here_path: Web/ForgeTrust.AppSurface.Web.Tailwind/README.md
            """);

        var artifacts = TestPathUtils.PathUnder(_root, "candidate-artifacts");
        Directory.CreateDirectory(artifacts);
        var webArchive = TestPathUtils.PathUnder(artifacts, "ForgeTrust.AppSurface.Web.1.2.3-preview.798.nupkg");
        await File.WriteAllTextAsync(webArchive, "test Web package bytes");
        var archive = TestPathUtils.PathUnder(artifacts, "ForgeTrust.AppSurface.Web.Tailwind.1.2.3-preview.798.nupkg");
        await File.WriteAllTextAsync(archive, "test package bytes");
        var artifactManifest = TestPathUtils.PathUnder(_root, "candidate-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport("1.2.3-preview.798",
            [
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Web", webProjectRelativePath, PackagePublishDecision.Publish, [], webArchive),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Web.Tailwind", projectRelativePath, PackagePublishDecision.Publish, [], archive)
            ]),
            artifacts,
            artifactManifest,
            CancellationToken.None);
        return (manifestPath, artifacts, artifactManifest);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
