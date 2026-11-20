using YamlDotNet.RepresentationModel;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableColdDaemonWorkflowTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-bridge")]
    [InlineData("empty-address")]
    [InlineData("malformed-address")]
    [InlineData("inspect-failure")]
    public async Task ColdDaemonSetup_RequiresAValidBridgeIPv4Address(string scenario)
    {
        // This workflow step is bash-only and runs on the Linux timing runner, so Windows has no executable proof here.
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "durable-cold-daemon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dockerPath = Path.Combine(root, "docker");
            await File.WriteAllTextAsync(dockerPath, """
                #!/usr/bin/env bash
                set -euo pipefail
                case "$1" in
                  pull) exit 0 ;;
                  create) printf 'fixture-container\n' ;;
                  start) exit 0 ;;
                  exec) exit 0 ;;
                  inspect)
                    format="$3"
                    if [[ "$format" == *Networks* && "$format" == *bridge* ]]; then
                      case "$DOCKER_FIXTURE" in
                        valid)
                          count=0
                          [[ ! -f "$DOCKER_FIXTURE_COUNT" ]] || count=$(<"$DOCKER_FIXTURE_COUNT")
                          count=$((count + 1))
                          printf '%s\n' "$count" > "$DOCKER_FIXTURE_COUNT"
                          printf '172.17.0.%s\n' "$count"
                          ;;
                        missing-bridge) printf 'template parsing error: missing bridge\n' >&2; exit 24 ;;
                        empty-address) printf '\n' ;;
                        malformed-address) printf '999.17.0.2\n' ;;
                        inspect-failure) exit 23 ;;
                      esac
                    else
                      # The legacy top-level NetworkSettings.IPAddress is absent on this daemon shape.
                      printf '\n'
                    fi
                    ;;
                  *) printf 'unexpected docker command: %s\n' "$*" >&2; exit 64 ;;
                esac
                """);
            File.SetUnixFileMode(dockerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // Exercise address selection without depending on GNU timeout being installed on a developer Mac.
            // The outer command runner still bounds this fixture; the workflow retains its actual timeouts.
            var timeoutPath = Path.Combine(root, "timeout");
            await File.WriteAllTextAsync(timeoutPath, "#!/usr/bin/env bash\nshift\nexec \"$@\"\n");
            File.SetUnixFileMode(timeoutPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var githubEnvironmentPath = Path.Combine(root, "github-env");
            await File.WriteAllTextAsync(githubEnvironmentPath, string.Empty);
            var dockerCountPath = Path.Combine(root, "docker-inspect-count");

            var workflowPath = Path.Combine(TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
                ".github", "workflows", "durable-template-evidence.yml");
            using var reader = File.OpenText(workflowPath);
            var yaml = new YamlStream();
            yaml.Load(reader);
            var workflow = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
            var jobs = Value(workflow, "jobs");
            var timing = Value(jobs, "template-timing");
            var steps = Assert.IsType<YamlSequenceNode>(Value(timing, "steps"));
            var step = Assert.Single(steps.Children.Cast<YamlMappingNode>(), item =>
                Scalar(Value(item, "name")) == "Create five private empty Docker daemons for cold samples");
            var script = Scalar(Value(step, "run"));

            var environment = new Dictionary<string, string?>
            {
                ["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["DOCKER_FIXTURE"] = scenario,
                ["DOCKER_FIXTURE_COUNT"] = dockerCountPath,
                ["RUNNER_TEMP"] = root,
                ["GITHUB_RUN_ID"] = "123",
                ["GITHUB_RUN_ATTEMPT"] = "2",
                ["GITHUB_ENV"] = githubEnvironmentPath
            };
            var result = await new CliWrapCommandRunner().RunAsync(
                new ExternalCommandRequest("bash", ["-c", script], root,
                    "durable cold daemon workflow fixture", "checking Docker bridge address lookup", 10_000, environment),
                CancellationToken.None);

            var expectedExit = scenario switch
            {
                "valid" => 0,
                "inspect-failure" => 23,
                "missing-bridge" => 24,
                _ => 1
            };
            Assert.Equal(expectedExit, result.ExitCode);
            if (scenario is "empty-address" or "malformed-address")
                Assert.Contains("Docker bridge IPv4 address is missing or invalid", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(Enumerable.Repeat("fixture-container", scenario == "valid" ? 5 : 1),
                await File.ReadAllLinesAsync(Path.Combine(root, "durable-cold-container-ids")));
            var hostsPath = Path.Combine(root, "durable-cold-hosts");
            if (scenario == "valid")
            {
                string[] expectedHosts = [
                    "tcp://172.17.0.1:2375",
                    "tcp://172.17.0.2:2375",
                    "tcp://172.17.0.3:2375",
                    "tcp://172.17.0.4:2375",
                    "tcp://172.17.0.5:2375"
                ];
                Assert.Equal(expectedHosts, await File.ReadAllLinesAsync(hostsPath));
                Assert.Equal(["DURABLE_COLD_HOSTS=" + string.Join(',', expectedHosts)],
                    await File.ReadAllLinesAsync(githubEnvironmentPath));
            }
            else
            {
                Assert.Empty(await File.ReadAllLinesAsync(hostsPath));
                Assert.DoesNotContain("DURABLE_COLD_HOSTS=", await File.ReadAllTextAsync(environment["GITHUB_ENV"]!));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static YamlNode Value(YamlNode node, string key)
    {
        var mapping = Assert.IsType<YamlMappingNode>(node);
        return Assert.Single(mapping.Children, pair => pair.Key is YamlScalarNode scalar && scalar.Value == key).Value;
    }

    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value ?? string.Empty;
}
