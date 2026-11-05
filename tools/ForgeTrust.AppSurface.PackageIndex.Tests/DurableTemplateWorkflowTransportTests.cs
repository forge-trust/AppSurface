using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateWorkflowTransportTests
{
    private static readonly string[] ReceiptSuffixes =
    [
        "linux-x64",
        "osx-arm64",
        "win-x64",
        "timing-primed",
        "timing-cold"
    ];

    private static readonly string[] NativeTools = ["initdb", "postgres", "pg_ctl", "psql"];

    private readonly string _repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);

    [Fact]
    public void PackageDiagnosticsUpload_KeepDurableReceiptAtItsBoundedPath()
    {
        var expectedPaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package-artifacts.yml"] = "${{ runner.temp }}/package-artifacts/durable-template-consumer-proof.json",
            ["package-gate.yml"] = "${{ runner.temp }}/package-evidence-*/package-checks/artifacts/durable-template-consumer-proof.json"
        };

        foreach (var (workflowName, expectedPath) in expectedPaths)
        {
            var workflow = ReadWorkflow(WorkflowPath(workflowName));
            var jobName = workflowName == "package-artifacts.yml" ? "package-artifacts" : "package-gate";
            var step = FindStep(workflow, jobName, "Upload package validation diagnostics");

            var paths = ReadPathLines(step);
            var durablePaths = paths.Where(path => path.Contains("durable-template", StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { expectedPath }, durablePaths);

            if (workflowName == "package-gate.yml")
            {
                Assert.DoesNotContain(paths, IsBroadPackageEvidenceUpload);
            }
        }
    }

    [Fact]
    public async Task ReceiptCollection_RequiresFiveUniqueCurrentAttemptReceipts()
    {
        if (OperatingSystem.IsWindows()) return;

        var workflow = ReadWorkflow(WorkflowPath("durable-template-evidence.yml"));
        var script = ReadScalar(FindStep(workflow, "collect", "Resolve exact receipt IDs from this successful invocation"), "run");
        var root = CreateTemporaryRoot("durable-template-receipts");
        try
        {
            var ghPath = TestPathUtils.PathUnder(root, "gh");
            WriteExecutable(ghPath, """
                #!/usr/bin/env bash
                set -euo pipefail
                query=''
                while (($#)); do
                  if [[ "$1" == --jq ]]; then
                    shift
                    query="${1:?missing jq query}"
                    break
                  fi
                  shift
                done
                exec jq -r "$query" "$PAGES"
                """);

            var pagesPath = TestPathUtils.PathUnder(root, "pages.json");
            var outputPath = TestPathUtils.PathUnder(root, "github-output");
            var environment = new Dictionary<string, string?>
            {
                ["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["GITHUB_RUN_ID"] = "99",
                ["GITHUB_RUN_ATTEMPT"] = "2",
                ["GITHUB_REPOSITORY"] = "fixture/repo",
                ["GITHUB_OUTPUT"] = outputPath,
                ["PAGES"] = pagesPath
            };

            foreach (var scenario in new[] { "all", "missing", "expired", "prior-attempt", "duplicate", "duplicate-masks-missing" })
            {
                var artifacts = ReceiptSuffixes.Select((suffix, index) =>
                    new ReceiptArtifact(index + 1, $"durable-template-99-2-{suffix}", false)).ToList();
                switch (scenario)
                {
                    case "missing":
                        artifacts.RemoveAt(artifacts.Count - 1);
                        break;
                    case "expired":
                        artifacts[^1] = artifacts[^1] with { Expired = true };
                        break;
                    case "prior-attempt":
                        artifacts[^1] = artifacts[^1] with { Name = "durable-template-99-1-timing-cold" };
                        break;
                    case "duplicate":
                        artifacts.Add(artifacts[^1] with { Id = 6 });
                        break;
                    case "duplicate-masks-missing":
                        artifacts[^1] = artifacts[^2] with { Id = 6 };
                        break;
                }

                await File.WriteAllTextAsync(pagesPath, JsonSerializer.Serialize(new
                {
                    artifacts = artifacts.Select(artifact => new
                    {
                        id = artifact.Id,
                        name = artifact.Name,
                        expired = artifact.Expired
                    })
                }));
                await File.WriteAllTextAsync(outputPath, string.Empty);
                var result = await RunBashAsync(script, root, environment);
                var succeeds = scenario == "all";

                Assert.True((result.ExitCode == 0) == succeeds, $"Scenario '{scenario}' returned {result.ExitCode}: {result.StandardError}");
                Assert.Equal(succeeds ? "ids=1,2,3,4,5\n" : string.Empty, await File.ReadAllTextAsync(outputPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CandidateAndReceiptTransport_CrossPublisherBoundariesAndBuildUsesThisTest()
    {
        var workflow = ReadWorkflow(WorkflowPath("durable-template-evidence.yml"));
        var source = File.ReadAllText(WorkflowPath("durable-template-evidence.yml"));
        Assert.Contains("needs: [native-template, template-timing]", source, StringComparison.Ordinal);
        Assert.Contains("core.autocrlf false", source, StringComparison.Ordinal);
        Assert.Contains("artifact-ids: ${{ inputs.producer_artifact_id }}", source, StringComparison.Ordinal);
        Assert.Contains("clock: [primed, cold]", source, StringComparison.Ordinal);
        Assert.Contains("docker rm --force --volumes \"$id\"", source, StringComparison.Ordinal);
        var receiptResolutionScript = ReadScalar(
            FindStep(workflow, "collect", "Resolve exact receipt IDs from this successful invocation"), "run");
        Assert.All(ReceiptSuffixes, suffix => Assert.Contains(suffix, receiptResolutionScript, StringComparison.Ordinal));

        foreach (var (workflowName, publishCommand) in new[]
        {
            ("nuget-prerelease-publish.yml", "publish-prerelease --durable-template-evidence"),
            ("nuget-stable-publish.yml", "publish-stable --durable-template-evidence")
        })
        {
            var publisher = File.ReadAllText(WorkflowPath(workflowName));
            Assert.Contains("artifact-ids: ${{ needs.durable-template-evidence.outputs.receipt_artifact_ids }}", publisher, StringComparison.Ordinal);
            Assert.True(publisher.IndexOf("verify-durable-template-evidence", StringComparison.Ordinal)
                < publisher.IndexOf(publishCommand, StringComparison.Ordinal),
                $"{workflowName} must verify Durable template evidence before publishing.");
        }

        var buildWorkflow = ReadWorkflow(WorkflowPath("build.yml"));
        var buildStep = FindStep(buildWorkflow, "build", "Verify Durable template release artifact transport");
        var buildCommand = ReadScalar(buildStep, "run");
        Assert.Contains("dotnet test", buildCommand, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~DurableTemplateWorkflowTransportTests", buildCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("python", buildCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MacAcquisition_ReturnsOnlyACompleteInstalledToolDirectory()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = CreateTemporaryRoot("durable-template-macos-acquisition");
        try
        {
            var brewPath = TestPathUtils.PathUnder(root, "brew");
            WriteExecutable(brewPath, """
                #!/usr/bin/env bash
                set -euo pipefail
                printf '%s\n' "$*" >> "$BREW_FIXTURE/calls.txt"
                prefix="$BREW_FIXTURE/installed formula"
                case "$*" in
                  "--prefix postgresql@16")
                    printf '%s\n' "$prefix"
                    exit 0
                    ;;
                  "--prefix --installed postgresql@16")
                    [[ -f "$BREW_FIXTURE/installed" ]] || exit 1
                    [[ "$BREW_CASE" != post-install-prefix-failure ]] || exit 4
                    case "$BREW_CASE" in
                      blank-prefix) printf '\n' ;;
                      relative-prefix) printf 'relative\n' ;;
                      multiline-prefix) printf '%s\nsecond-prefix\n' "$prefix" ;;
                      *) printf '%s\n' "$prefix" ;;
                    esac
                    exit 0
                    ;;
                  "install postgresql@16")
                    printf '%s\n' 'installation progress on stdout'
                    printf '%s\n' 'installation progress on stderr' >&2
                    [[ "$BREW_CASE" != install-failure ]] || exit 3
                    mkdir -p "$prefix/bin"
                    for tool in initdb postgres pg_ctl psql; do
                      [[ "$BREW_CASE" != incomplete-install || "$tool" != psql ]] || continue
                      printf '%s\n' 'fixture tool' > "$prefix/bin/$tool"
                    done
                    : > "$BREW_FIXTURE/installed"
                    exit 0
                    ;;
                  *)
                    printf '%s\n' 'unexpected brew arguments' >&2
                    exit 64
                    ;;
                esac
                """);

            var installedCases = new HashSet<string>(StringComparer.Ordinal)
            {
                "installed", "symlink-prefix", "incomplete-installed", "directory-tool",
                "blank-prefix", "relative-prefix", "multiline-prefix"
            };
            var cases = new[]
            {
                "missing", "installed", "symlink-prefix", "install-failure", "post-install-prefix-failure",
                "incomplete-install", "incomplete-installed", "directory-tool", "blank-prefix", "relative-prefix", "multiline-prefix"
            };

            foreach (var scenario in cases)
            {
                var fixture = TestPathUtils.PathUnder(root, scenario);
                Directory.CreateDirectory(fixture);
                var prefix = TestPathUtils.PathUnder(fixture, "installed formula");
                var expectedBin = prefix;
                if (installedCases.Contains(scenario))
                {
                    await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture, "installed"), "installed");
                    if (scenario == "symlink-prefix")
                    {
                        var physicalKeg = TestPathUtils.PathUnder(fixture, "physical keg");
                        Directory.CreateDirectory(physicalKeg);
                        Directory.CreateSymbolicLink(prefix, physicalKeg);
                        expectedBin = physicalKeg;
                    }

                    if (scenario is "installed" or "symlink-prefix" or "incomplete-installed" or "directory-tool")
                    {
                        var bin = TestPathUtils.PathUnder(expectedBin, "bin");
                        Directory.CreateDirectory(bin);
                        foreach (var tool in NativeTools)
                        {
                            if (scenario == "incomplete-installed" && tool == "psql") continue;
                            var toolPath = TestPathUtils.PathUnder(bin, tool);
                            if (scenario == "directory-tool" && tool == "pg_ctl") Directory.CreateDirectory(toolPath);
                            else await File.WriteAllTextAsync(toolPath, "fixture tool");
                        }
                    }
                }

                var callsPath = TestPathUtils.PathUnder(fixture, "calls.txt");
                var environment = new Dictionary<string, string?>
                {
                    ["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["BREW_FIXTURE"] = fixture,
                    ["BREW_CASE"] = scenario
                };

                if (scenario == "missing")
                {
                    var barePrefix = await RunCommandAsync("brew", ["--prefix", "postgresql@16"], root, environment);
                    Assert.Equal(0, barePrefix.ExitCode);
                    Assert.False(Directory.Exists(prefix));
                    File.Delete(callsPath);
                }

                var helper = ScriptPath("resolve-macos-native-postgresql.sh");
                var result = await RunCommandAsync("bash", [helper], root, environment);
                var succeeded = scenario is "missing" or "installed" or "symlink-prefix";
                Assert.True((result.ExitCode == 0) == succeeded, $"Scenario '{scenario}' returned {result.ExitCode}: {result.StandardError}");
                Assert.Equal(succeeded ? TestPathUtils.PathUnder(expectedBin, "bin") + "\n" : string.Empty, result.StandardOutput);

                var expectedCalls = new List<string> { "--prefix --installed postgresql@16" };
                if (!installedCases.Contains(scenario))
                {
                    expectedCalls.Add("install postgresql@16");
                    if (scenario != "install-failure") expectedCalls.Add("--prefix --installed postgresql@16");
                }
                Assert.Equal(expectedCalls, await File.ReadAllLinesAsync(callsPath));

                if (scenario is "blank-prefix" or "relative-prefix" or "multiline-prefix")
                    Assert.Contains("installed prefix is missing or invalid", result.StandardError, StringComparison.Ordinal);
                if (scenario is "incomplete-install" or "incomplete-installed" or "directory-tool")
                    Assert.Contains("tool set is incomplete", result.StandardError, StringComparison.Ordinal);
                if (scenario == "missing")
                {
                    Assert.Contains("installation progress on stdout", result.StandardError, StringComparison.Ordinal);
                    Assert.Contains("installation progress on stderr", result.StandardError, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeWorkflow_ValidatesAllToolsAfterAcquisitionAndKeepsTimeout()
    {
        var workflow = ReadWorkflow(WorkflowPath("durable-template-evidence.yml"));
        var nativeJob = GetMappingValue(GetMappingValue(workflow, "jobs"), "native-template");
        var nativeSteps = GetSequenceValue(nativeJob, "steps").Children.Cast<YamlMappingNode>().ToArray();
        var step = FindStep(workflow, "native-template", "Resolve private native PostgreSQL tools");
        var script = ReadScalar(step, "run");
        Assert.Contains("$bin = & bash .github/scripts/resolve-macos-native-postgresql.sh", script, StringComparison.Ordinal);
        Assert.Contains("if ($LASTEXITCODE -ne 0) { throw 'Native PostgreSQL acquisition failed.' }", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("resolve-macos-native-postgresql.sh", StringComparison.Ordinal)
            < script.IndexOf("@('initdb', 'postgres', 'pg_ctl', 'psql')", StringComparison.Ordinal));
        Assert.Contains("-PathType Leaf", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("-PathType Leaf", StringComparison.Ordinal)
            < script.IndexOf("NATIVE_PG_BIN=$bin", StringComparison.Ordinal));
        Assert.Equal("30", ReadScalar(nativeJob, "timeout-minutes"));
        Assert.NotEmpty(nativeSteps);
    }

    private static bool IsBroadPackageEvidenceUpload(string path)
    {
        return path.EndsWith("/package-checks/**", StringComparison.Ordinal)
            || path.EndsWith("/package-checks/artifacts/**", StringComparison.Ordinal);
    }

    private async Task<ExternalCommandResult> RunBashAsync(
        string script,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        return await RunCommandAsync("bash", ["-c", script], workingDirectory, environment);
    }

    private static Task<ExternalCommandResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment)
    {
        return new CliWrapCommandRunner().RunAsync(
            new ExternalCommandRequest(executable, arguments, workingDirectory,
                "durable template workflow transport fixture", "running a bounded workflow fixture", 10_000, environment),
            CancellationToken.None);
    }

    private static string CreateTemporaryRoot(string name)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && (temporary.StartsWith("/var/", StringComparison.Ordinal) || temporary.StartsWith("/tmp/", StringComparison.Ordinal)))
            temporary = "/private" + temporary;
        var root = TestPathUtils.PathUnder(temporary, name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private string WorkflowPath(string name) => TestPathUtils.PathUnder(_repositoryRoot, ".github", "workflows", name);

    private string ScriptPath(string name) => TestPathUtils.PathUnder(_repositoryRoot, ".github", "scripts", name);

    private static YamlMappingNode ReadWorkflow(string path)
    {
        using var reader = File.OpenText(path);
        var stream = new YamlStream();
        stream.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
    }

    private static YamlMappingNode FindStep(YamlMappingNode workflow, string jobName, string stepName)
    {
        var job = GetMappingValue(GetMappingValue(workflow, "jobs"), jobName);
        var steps = GetSequenceValue(job, "steps");
        return Assert.Single(steps.Children.Cast<YamlMappingNode>(), step =>
            string.Equals(ReadScalar(step, "name"), stepName, StringComparison.Ordinal));
    }

    private static string[] ReadPathLines(YamlMappingNode step)
    {
        var paths = ReadScalar(GetMappingValue(step, "with"), "path");
        return paths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ReadScalar(YamlNode mapping, string key)
    {
        return Assert.IsType<YamlScalarNode>(GetMappingValue(mapping, key)).Value ?? string.Empty;
    }

    private static YamlNode GetMappingValue(YamlNode node, string key)
    {
        var mapping = Assert.IsType<YamlMappingNode>(node);
        return Assert.Single(mapping.Children, pair =>
            pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal)).Value;
    }

    private static YamlSequenceNode GetSequenceValue(YamlNode node, string key)
    {
        return Assert.IsType<YamlSequenceNode>(GetMappingValue(node, key));
    }

    private sealed record ReceiptArtifact(int Id, string Name, bool Expired);
}
