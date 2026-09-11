using System.Diagnostics;
using System.Globalization;
using ForgeTrust.AppSurface.Testing;

/// <summary>Locks the one-command proof to its local-only, explicit-migration safety boundary.</summary>
public sealed class DurablePostgreSqlLocalProofScriptTests
{
    [Fact]
    public void Script_composes_the_required_local_proof_with_ephemeral_credentials_and_no_startup_ddl()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "examples",
            "durable-postgresql",
            "run-local-proof.sh");
        var script = File.ReadAllText(scriptPath);

        Assert.Contains("check-prerequisites.sh", script, StringComparison.Ordinal);
        Assert.Contains("postgres:16.5@sha256:", script, StringComparison.Ordinal);
        Assert.Contains("durable schema apply", script, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_MIGRATION_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("Durable/configure-postgresql-roles.sql", script, StringComparison.Ordinal);
        Assert.Contains("schema-bootstrap-dev", script, StringComparison.Ordinal);
        Assert.Contains("verify-local", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build", script, StringComparison.Ordinal);
        Assert.Contains("-m:1", script, StringComparison.Ordinal);
        Assert.Contains("-p:UseSharedCompilation=false", script, StringComparison.Ordinal);
        Assert.Contains("--no-build", script, StringComparison.Ordinal);
        Assert.Contains("docker rm --force", script, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_PASSWORD", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$MIGRATION_OWNER_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$DISPATCHER_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$RUNTIME_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("PASSWORD '$RETENTION_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("printf '%s\\n' \"$ROLE_SQL\" > \"$ROLE_SQL_FILE\"", script, StringComparison.Ordinal);
        Assert.Contains("docker exec -i", script, StringComparison.Ordinal);
        Assert.Contains("unset ROLE_SQL", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-c \"$ROLE_SQL\"", script, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS=420", script, StringComparison.Ordinal);
        Assert.Contains("MAX_TIMEOUT_SECONDS=86400", script, StringComparison.Ordinal);
        Assert.Contains("set -m", script, StringComparison.Ordinal);
        Assert.Contains("FOREGROUND_PID_FILE", script, StringComparison.Ordinal);
        Assert.Contains("run_foreground", script, StringComparison.Ordinal);
        Assert.Contains("kill -TERM \"$$\"", script, StringComparison.Ordinal);
        Assert.Contains("signal_process_group -KILL \"$pid\"", script, StringComparison.Ordinal);
        Assert.Contains("cleanup_container", script, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1::5432", script, StringComparison.Ordinal);
        Assert.Contains("docker port \"$CONTAINER_NAME\" 5432/tcp", script, StringComparison.Ordinal);
        Assert.Contains("APPSURFACE_DURABLE_PREREQUISITE_SKIP_PORT_CHECK=true", script, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", script, StringComparison.Ordinal);
        Assert.Contains("trap interrupt INT TERM", script, StringComparison.Ordinal);
        Assert.DoesNotContain("POSTGRES_HOST_AUTH_METHOD=trust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("set -x", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_deadline_terminates_a_hung_child_process_group_without_docker()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The local proof script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "examples",
            "durable-postgresql",
            "run-local-proof.sh");
        var temporaryRoot = Directory.CreateTempSubdirectory("appsurface-durable-proof-watchdog-").FullName;
        var fakeBin = Directory.CreateDirectory(Path.Combine(temporaryRoot, "bin")).FullName;
        var childPidFile = Path.Combine(temporaryRoot, "child.pid");
        var heartbeatFile = Path.Combine(temporaryRoot, "heartbeat");
        var cleanupHeartbeatFile = Path.Combine(temporaryRoot, "cleanup-heartbeat");

        try
        {
            WriteExecutable(
                fakeBin,
                "bash",
                """
                #!/bin/sh
                case "${1:-}" in
                  */examples/durable-postgresql/check-prerequisites.sh)
                    exit 0
                    ;;
                esac
                exec /bin/bash "$@"
                """);
            WriteExecutable(
                fakeBin,
                "docker",
                """
                #!/bin/sh
                case "${1:-}" in
                  info|run)
                    exit 0
                    ;;
                  rm)
                    while :; do
                      printf x >> "$APPSURFACE_TEST_CLEANUP_HEARTBEAT_FILE"
                      sleep 0.05
                    done
                    ;;
                  exec)
                    case "$*" in
                      *gen_random_uuid*)
                        printf '%s\n' '00000000-0000-0000-0000-000000000001'
                        ;;
                    esac
                    exit 0
                    ;;
                esac
                exit 0
                """);
            WriteExecutable(
                fakeBin,
                "dotnet",
                """
                #!/bin/sh
                if [ "${1:-}" = "--version" ]; then
                  printf '%s\n' '10.0.0'
                  exit 0
                fi
                case "$*" in
                  *verify-local*)
                    (
                      while :; do
                        printf x >> "$APPSURFACE_TEST_HEARTBEAT_FILE"
                        sleep 0.05
                      done
                    ) &
                    child_pid=$!
                    printf '%s\n' "$child_pid" > "$APPSURFACE_TEST_CHILD_PID_FILE"
                    wait "$child_pid"
                    ;;
                esac
                exit 0
                """);

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                fakeBin,
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["APPSURFACE_DURABLE_LOCAL_PORT"] = "54349";
            startInfo.Environment["APPSURFACE_DURABLE_LOCAL_PROOF_TIMEOUT_SECONDS"] = "2";
            startInfo.Environment["APPSURFACE_TEST_CHILD_PID_FILE"] = childPidFile;
            startInfo.Environment["APPSURFACE_TEST_HEARTBEAT_FILE"] = heartbeatFile;
            startInfo.Environment["APPSURFACE_TEST_CLEANUP_HEARTBEAT_FILE"] = cleanupHeartbeatFile;

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(startInfo)!;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                throw;
            }

            stopwatch.Stop();
            var standardError = await process.StandardError.ReadToEndAsync();
            // Bash may preserve the interrupt trap's explicit 130 or surface the watchdog's SIGTERM as 143.
            // The deadline diagnostic and descendant-cleanup assertions below are the portable contract.
            Assert.True(
                process.ExitCode is 130 or 143,
                $"Expected an interrupted exit (130 or 143), but received {process.ExitCode}.");
            Assert.Contains("exceeded its 2-second deadline", standardError, StringComparison.Ordinal);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7));

            var childPid = await File.ReadAllTextAsync(childPidFile);
            Assert.Matches("^[0-9]+\\n?$", childPid);
            var heartbeatLength = new FileInfo(heartbeatFile).Length;
            var cleanupHeartbeatLength = new FileInfo(cleanupHeartbeatFile).Length;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Equal(heartbeatLength, new FileInfo(heartbeatFile).Length);
            Assert.Equal(cleanupHeartbeatLength, new FileInfo(cleanupHeartbeatFile).Length);
            Assert.True(
                await WaitForProcessExitAsync(int.Parse(childPid, CultureInfo.InvariantCulture)),
                $"The local-proof command child {childPid.Trim()} remained alive after cleanup.");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Script_has_valid_bash_syntax()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Bash syntax validation runs only on Unix hosts.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "examples",
            "durable-postgresql",
            "run-local-proof.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            throw;
        }

        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(process.ExitCode == 0, error);
    }

    [Fact]
    public async Task Adoption_measurement_deadline_terminates_each_hung_process_group_and_records_timeouts()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The adoption measurement script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "evidence",
            "measure-issue-794-adoption.sh");
        var temporaryRoot = Directory.CreateTempSubdirectory("appsurface-durable-measurement-watchdog-").FullName;
        var heartbeatFile = Path.Combine(temporaryRoot, "heartbeat");
        try
        {
            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = repositoryRoot,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("hung-journey");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("/bin/bash");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                "while :; do printf x >> \"$APPSURFACE_TEST_HEARTBEAT_FILE\"; sleep 0.05; done");
            startInfo.Environment["APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS"] = "1";
            startInfo.Environment["APPSURFACE_TEST_HEARTBEAT_FILE"] = heartbeatFile;

            using var process = Process.Start(startInfo)!;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                throw;
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            Assert.Equal(1, process.ExitCode);
            Assert.Equal(2, CountOccurrences(standardOutput, "| timeout |"));
            Assert.Equal(2, CountOccurrences(standardError, "exceeded its 1-second deadline"));
            var heartbeatLength = new FileInfo(heartbeatFile).Length;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.Equal(heartbeatLength, new FileInfo(heartbeatFile).Length);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Adoption_measurement_rejects_an_invalid_deadline()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The adoption measurement script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "evidence",
            "measure-issue-794-adoption.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("invalid-deadline");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("/usr/bin/true");
        startInfo.Environment["APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS"] = "0";

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains(
            "APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS must be an integer from 1 through 86400.",
            standardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("86401")]
    [InlineData("999999999999999999999")]
    public async Task Adoption_measurement_rejects_deadlines_above_the_portable_sleep_bound(string deadline)
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The adoption measurement script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "evidence",
            "measure-issue-794-adoption.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("invalid-deadline");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("/usr/bin/true");
        startInfo.Environment["APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS"] = deadline;

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains(
            "APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS must be an integer from 1 through 86400.",
            standardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("journey|table")]
    [InlineData("journey`code")]
    [InlineData("journey\nnewline")]
    public async Task Adoption_measurement_rejects_labels_that_are_not_safe_markdown_identifiers(string label)
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The adoption measurement script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "evidence",
            "measure-issue-794-adoption.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(label);
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("/usr/bin/true");

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.Contains(
            "LABEL must contain only letters, digits, periods, underscores, and hyphens",
            standardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adoption_measurement_accepts_a_safe_label_and_records_it_in_markdown()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "The adoption measurement script is a Unix Bash entry point.");
        }

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var scriptPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "evidence",
            "measure-issue-794-adoption.sh");
        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("journey.v1-2_test");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("/usr/bin/true");

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var standardOutput = await process.StandardOutput.ReadToEndAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains(
            "| `journey.v1-2_test` | 1 |",
            standardOutput,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    private static void WriteExecutable(string directory, string name, string contents)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The local proof fixture requires Unix executable permissions.");
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);
    }
}
