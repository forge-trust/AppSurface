using System.Diagnostics;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class ExternalActivationVerificationScriptTests
{
    private const string ConnectionSecret = "Host=activation-test.invalid;Username=proof-user;Password=never-print-this";

    [Fact]
    public void Packed_proof_uses_the_fixed_host_sources_and_the_local_Observability_package_closure()
    {
        var script = ReadScript();

        Assert.Contains("Observability/ForgeTrust.AppSurface.Observability/ForgeTrust.AppSurface.Observability.csproj", script, StringComparison.Ordinal);
        Assert.Contains("\"ForgeTrust.AppSurface.Observability\"", script, StringComparison.Ordinal);
        Assert.Contains("verify_appsurface_assets_closure \"$consumer_host_dir/obj/project.assets.json\"", script, StringComparison.Ordinal);
        Assert.Contains("verify_appsurface_assets_closure \"$consumer_tests_dir/obj/project.assets.json\"", script, StringComparison.Ordinal);
        Assert.Contains("local consumer_examples_dir=\"$consumer_root/examples\"", script, StringComparison.Ordinal);
        Assert.Contains("local consumer_host_dir=\"$consumer_examples_dir/durable-external-activation\"", script, StringComparison.Ordinal);
        Assert.Contains("local consumer_tests_dir=\"$consumer_examples_dir/durable-external-activation.tests\"", script, StringComparison.Ordinal);
        Assert.Contains("libraries.get(key, {}).get(\"type\") != \"package\"", script, StringComparison.Ordinal);
        Assert.Contains("verify_restored_package \"$package_id\"", script, StringComparison.Ordinal);
        Assert.Contains("local host_source_dir=\"$ROOT_DIR/examples/durable-external-activation\"", script, StringComparison.Ordinal);
        Assert.Contains("local tests_source_dir=\"$ROOT_DIR/examples/durable-external-activation.tests\"", script, StringComparison.Ordinal);
        Assert.Contains("DurableExternalActivationExample.csproj", script, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj", script, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlIntegrationTestDatabase.cs", script, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlTestContainerImage.cs", script, StringComparison.Ordinal);
        Assert.Contains("compile_item.set(\"Include\", str(support_file))", script, StringComparison.Ordinal);
        Assert.Contains("compile_item.set(\"Link\", f\"PackedSupport/{support_file.name}\")", script, StringComparison.Ordinal);
        Assert.Contains("Testcontainers.PostgreSql", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NET.Test.Sdk", script, StringComparison.Ordinal);
        Assert.Contains("AppSurfacePackageVersion", script, StringComparison.Ordinal);
        Assert.Contains("retains an AppSurface ProjectReference", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test \"$project\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter", script, StringComparison.Ordinal);
        Assert.Contains("validate_strict_postgres_trx \"$trx_file\"", script, StringComparison.Ordinal);
        Assert.Contains("requires passing tests, zero skips, and at least one test", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tRuE", true, false, "pinned Testcontainers image (strict CI)")]
    [InlineData("false", true, true, "explicit local PostgreSQL 16+ override (strict)")]
    [InlineData("false", false, false, "pinned Testcontainers image (strict local)")]
    [InlineData(null, true, true, "explicit local PostgreSQL 16+ override (strict)")]
    public async Task Database_test_child_uses_the_strict_mode_without_mutating_parent_environment(
        string? parentCi,
        bool hasConnection,
        bool expectChildConnection,
        string expectedMode)
    {
        var result = await RunEnvironmentHelperAsync(
            parentCi,
            hasConnection ? ConnectionSecret : string.Empty,
            commandExitCode: 0,
            expectedChildConnection: expectChildConnection);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedMode, result.Output, StringComparison.Ordinal);
        Assert.False(result.Output.Contains(ConnectionSecret, StringComparison.Ordinal), "The PostgreSQL connection was printed.");
    }

    [Fact]
    public async Task Whitespace_only_connection_uses_the_same_strict_container_path_as_the_fixture()
    {
        var result = await RunEnvironmentHelperAsync(
            parentCi: "false",
            parentConnection: " \t ",
            commandExitCode: 0,
            expectedChildConnection: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pinned Testcontainers image (strict local)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Database_test_child_failure_remains_failure_and_does_not_print_connection_data()
    {
        const int childExitCode = 37;
        var result = await RunEnvironmentHelperAsync(
            parentCi: "false",
            parentConnection: ConnectionSecret,
            commandExitCode: childExitCode,
            expectedChildConnection: true);

        Assert.Equal(childExitCode, result.ExitCode);
        Assert.False(result.Output.Contains(ConnectionSecret, StringComparison.Ordinal), "The PostgreSQL connection was printed.");

        var script = ReadScript();
        Assert.Contains("if [[ \"$test_exit_code\" -ne 0 ]]; then", script, StringComparison.Ordinal);
        Assert.Contains("print_sanitized_activation_test_log \"$log_file\" >&2", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cat \"$activation_log\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_database_test_output_is_sanitized_before_the_wrapper_emits_it()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The packed consumer proof script is a Unix Bash entry point.");
        }

        const string separatePassword = "private-prefix private-suffix";
        var script = ReadScript();
        var environmentRunner = ExtractMarkedBlock(
            script,
            "# BEGIN strict PostgreSQL test process environment helper",
            "# END strict PostgreSQL test process environment helper");
        var sanitizer = ExtractMarkedBlock(
            script,
            "# BEGIN activation test output sanitizer",
            "# END activation test output sanitizer");
        var testRunner = ExtractMarkedBlock(
            script,
            "# BEGIN external activation test runner",
            "# END external activation test runner");
        var temporaryDirectory = CreateTemporaryDirectory();
        var fakeBinDirectory = Path.Combine(temporaryDirectory, "bin");
        var fakeDotnetPath = Path.Combine(fakeBinDirectory, "dotnet");
        var harnessPath = Path.Combine(temporaryDirectory, "failed-test.sh");
        var projectPath = Path.Combine(temporaryDirectory, "activation.tests.csproj");
        var logPath = Path.Combine(temporaryDirectory, "activation-test.log");
        Directory.CreateDirectory(fakeBinDirectory);

        try
        {
            await File.WriteAllTextAsync(
                fakeDotnetPath,
                "#!/usr/bin/env bash\nprintf 'configured server was %s\\nPassword = %s; retry failed\\n' \"$ACTIVATION_TEST_CONNECTION_FOR_LOG\" \"$ACTIVATION_TEST_PASSWORD_FOR_LOG\"\nexit 37\n");
            File.SetUnixFileMode(
                fakeDotnetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var harness = $$"""
                set -euo pipefail
                fail() { echo "Packed Durable consumer verification failed: $1" >&2; exit 1; }
                {{environmentRunner}}
                {{sanitizer}}
                {{testRunner}}
                run_external_activation_test_project "$1" "$2" "Contract fake failure"
                """;
            await File.WriteAllTextAsync(harnessPath, harness);

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add(logPath);
            startInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                fakeBinDirectory,
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["APPSURFACE_POSTGRES_TEST_CONNECTION"] = ConnectionSecret;
            startInfo.Environment["ACTIVATION_TEST_CONNECTION_FOR_LOG"] = ConnectionSecret;
            startInfo.Environment["ACTIVATION_TEST_PASSWORD_FOR_LOG"] = separatePassword;

            var result = await RunProcessAsync(startInfo);
            var output = result.Output + result.Error;

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Password=<redacted>", output, StringComparison.Ordinal);
            Assert.Contains("diagnostic output omitted", output, StringComparison.Ordinal);
            Assert.DoesNotContain(ConnectionSecret, output, StringComparison.Ordinal);
            Assert.DoesNotContain(separatePassword, output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-prefix", output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-suffix", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 0, "Passed", 0)]
    [InlineData(1, 0, 0, 1, 0, "NotExecuted", 1)]
    [InlineData(1, 1, 0, 0, 0, "NotExecuted", 1)]
    [InlineData(0, 0, 0, 0, 0, "", 1)]
    [InlineData(1, 0, 1, 0, 0, "Failed", 1)]
    [InlineData(1, 1, 0, 0, 1, "Passed", 1)]
    [InlineData(1, 1, 0, 0, 0, "Inconclusive", 1)]
    public async Task Strict_trx_gate_requires_real_passing_tests_and_rejects_skips_or_failures(
        int total,
        int passed,
        int failed,
        int notExecuted,
        int error,
        string outcome,
        int expectedExitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The packed consumer proof script is a Unix Bash entry point.");
        }

        var script = ReadScript();
        var validator = ExtractMarkedBlock(
            script,
            "# BEGIN strict PostgreSQL TRX proof helper",
            "# END strict PostgreSQL TRX proof helper");
        var temporaryDirectory = CreateTemporaryDirectory();
        var trxPath = Path.Combine(temporaryDirectory, "activation-proof.trx");
        var harnessPath = Path.Combine(temporaryDirectory, "validate-trx.sh");
        var resultNode = outcome.Length == 0 ? string.Empty : $"<UnitTestResult outcome=\"{outcome}\" />";
        var trx = $"<TestRun><ResultSummary><Counters total=\"{total}\" passed=\"{passed}\" failed=\"{failed}\" error=\"{error}\" notExecuted=\"{notExecuted}\" /></ResultSummary><Results>{resultNode}</Results></TestRun>";

        try
        {
            await File.WriteAllTextAsync(trxPath, trx);
            await File.WriteAllTextAsync(harnessPath, $"{validator}{Environment.NewLine}validate_strict_postgres_trx \"$1\"{Environment.NewLine}");
            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(trxPath);

            var result = await RunProcessAsync(startInfo);

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.DoesNotContain(ConnectionSecret, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(ConnectionSecret, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Failure_diagnostics_redact_explicit_connections_and_password_fields()
    {
        var script = ReadScript();
        var sanitizer = ExtractMarkedBlock(
            script,
            "# BEGIN activation test output sanitizer",
            "# END activation test output sanitizer");
        var temporaryDirectory = CreateTemporaryDirectory();
        var logPath = Path.Combine(temporaryDirectory, "activation-test.log");
        var harnessPath = Path.Combine(temporaryDirectory, "sanitize.sh");
        const string separatePassword = "another-private-marker";

        try
        {
            await File.WriteAllTextAsync(
                logPath,
                $"configured server was {ConnectionSecret}{Environment.NewLine}Password={separatePassword}; retry failed{Environment.NewLine}");
            await File.WriteAllTextAsync(harnessPath, $"{sanitizer}{Environment.NewLine}print_sanitized_activation_test_log \"$1\"{Environment.NewLine}");

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(logPath);
            startInfo.Environment["APPSURFACE_POSTGRES_TEST_CONNECTION"] = ConnectionSecret;

            var result = await RunProcessAsync(startInfo);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Password=<redacted>", result.Output, StringComparison.Ordinal);
            Assert.Contains("diagnostic output omitted", result.Output, StringComparison.Ordinal);
            Assert.False(result.Output.Contains(ConnectionSecret, StringComparison.Ordinal), "A connection string was printed.");
            Assert.False(result.Output.Contains(separatePassword, StringComparison.Ordinal), "A password was printed.");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Password = private-prefix private-suffix; retry failed")]
    [InlineData("pWd = 'private-prefix ''private-suffix'; retry failed")]
    [InlineData("PASSWORD = \"private-prefix \"\"private-suffix;still-secret\"; retry failed")]
    [InlineData("Pwd = 'private-prefix;private-suffix; retry failed")]
    [InlineData("Password=private-prefix; Pwd=private-suffix; retry failed")]
    [InlineData("Password = 'private-prefix\nprivate-suffix'; retry failed")]
    public async Task Failure_diagnostics_omit_password_output_when_field_boundaries_are_ambiguous(string diagnostic)
    {
        var sanitizer = ExtractMarkedBlock(
            ReadScript(),
            "# BEGIN activation test output sanitizer",
            "# END activation test output sanitizer");
        var temporaryDirectory = CreateTemporaryDirectory();
        var logPath = Path.Combine(temporaryDirectory, "activation-test.log");
        var harnessPath = Path.Combine(temporaryDirectory, "sanitize.sh");

        try
        {
            await File.WriteAllTextAsync(logPath, $"{diagnostic}{Environment.NewLine}safe failure summary{Environment.NewLine}");
            await File.WriteAllTextAsync(harnessPath, $"{sanitizer}{Environment.NewLine}print_sanitized_activation_test_log \"$1\"{Environment.NewLine}");
            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            };
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add(logPath);

            var result = await RunProcessAsync(startInfo);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("=<redacted>", result.Output, StringComparison.Ordinal);
            Assert.Contains("diagnostic output omitted", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-prefix", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("private-suffix", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("still-secret", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("retry failed", result.Output, StringComparison.Ordinal);
            Assert.Empty(result.Error);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void Strict_child_environment_matches_the_existing_fixture_override_and_skip_rules()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var fixturePath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable",
            "ForgeTrust.AppSurface.Durable.PostgreSql.Tests",
            "PostgreSqlIntegrationTestDatabase.cs");
        var fixture = File.ReadAllText(fixturePath);
        var script = ReadScript();

        Assert.Contains(
            "string.IsNullOrWhiteSpace(configured)\n            ? await CreateDatabaseAsync(await GetSharedContainerServerAsync())\n            : await CreateFromConnectionStringAsync(configured);",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains("if (skipRequested && !runningInCi)", fixture, StringComparison.Ordinal);
        Assert.Contains("env -u APPSURFACE_POSTGRES_TEST_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("-u APPSURFACE_POSTGRES_TEST_ALLOW_SKIP", script, StringComparison.Ordinal);
        Assert.Contains("env -u APPSURFACE_POSTGRES_TEST_ALLOW_SKIP CI=true \"$@\"", script, StringComparison.Ordinal);
        Assert.Contains("[[ \"${APPSURFACE_POSTGRES_TEST_CONNECTION:-}\" =~ [^[:space:]] ]]", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_has_valid_bash_syntax()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The packed consumer proof script is a Unix Bash entry point.");
        }

        var startInfo = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(ScriptPath());

        var result = await RunProcessAsync(startInfo);

        Assert.True(result.ExitCode == 0, result.Error);
    }

    private static async Task<(int ExitCode, string Output)> RunEnvironmentHelperAsync(
        string? parentCi,
        string parentConnection,
        int commandExitCode,
        bool expectedChildConnection)
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The packed consumer proof script is a Unix Bash entry point.");
        }

        var script = ReadScript();
        var runner = ExtractMarkedBlock(
            script,
            "# BEGIN strict PostgreSQL test process environment helper",
            "# END strict PostgreSQL test process environment helper");
        var temporaryDirectory = CreateTemporaryDirectory();
        var harnessPath = Path.Combine(temporaryDirectory, "runner-contract.sh");
        var harness = $$"""
            set +e
            {{runner}}
            run_strict_postgres_test_process bash -c '
              if [[ "${CI-}" != "true" ]] || [[ -n "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP+x}" ]]; then exit 91; fi
              if [[ "$ACTIVATION_TEST_EXPECTED_CONNECTION_SET" == "present" ]]; then
                if [[ "${APPSURFACE_POSTGRES_TEST_CONNECTION+x}" != "x" ]] || [[ "$APPSURFACE_POSTGRES_TEST_CONNECTION" != "$ACTIVATION_TEST_EXPECTED_CONNECTION" ]]; then exit 92; fi
              elif [[ -n "${APPSURFACE_POSTGRES_TEST_CONNECTION+x}" ]]; then
                exit 93
              fi
              exit "$ACTIVATION_TEST_COMMAND_EXIT_CODE"
            '
            child_status=$?
            if [[ "${APPSURFACE_POSTGRES_TEST_CONNECTION+x}" != "x" ]] || [[ "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP+x}" != "x" ]]; then exit 96; fi
            if [[ "$ACTIVATION_TEST_PARENT_CI_SET" == "present" ]]; then
              if [[ "${CI+x}" != "x" ]] || [[ "${CI-}" != "$ACTIVATION_TEST_PARENT_CI" ]]; then exit 95; fi
            elif [[ -n "${CI+x}" ]]; then
              exit 95
            fi
            if [[ "$child_status" -ne "$ACTIVATION_TEST_EXPECTED_EXIT_CODE" ]]; then exit 94; fi
            if [[ "${CI-}" != "$ACTIVATION_TEST_PARENT_CI" ]] \
              || [[ "${APPSURFACE_POSTGRES_TEST_CONNECTION-}" != "$ACTIVATION_TEST_PARENT_CONNECTION" ]] \
              || [[ "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP-}" != "$ACTIVATION_TEST_PARENT_SKIP" ]]; then
              exit 95
            fi
            exit "$child_status"
            """;

        try
        {
            await File.WriteAllTextAsync(harnessPath, harness);
            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory),
            };
            startInfo.ArgumentList.Add(harnessPath);
            if (parentCi is null)
            {
                startInfo.Environment.Remove("CI");
            }
            else
            {
                startInfo.Environment["CI"] = parentCi;
            }
            startInfo.Environment["APPSURFACE_POSTGRES_TEST_CONNECTION"] = parentConnection;
            startInfo.Environment["APPSURFACE_POSTGRES_TEST_ALLOW_SKIP"] = "true";
            startInfo.Environment["ACTIVATION_TEST_EXPECTED_CONNECTION_SET"] = expectedChildConnection ? "present" : "unset";
            startInfo.Environment["ACTIVATION_TEST_EXPECTED_CONNECTION"] = parentConnection;
            startInfo.Environment["ACTIVATION_TEST_COMMAND_EXIT_CODE"] = commandExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["ACTIVATION_TEST_EXPECTED_EXIT_CODE"] = commandExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["ACTIVATION_TEST_PARENT_CI_SET"] = parentCi is null ? "unset" : "present";
            startInfo.Environment["ACTIVATION_TEST_PARENT_CI"] = parentCi ?? string.Empty;
            startInfo.Environment["ACTIVATION_TEST_PARENT_CONNECTION"] = parentConnection;
            startInfo.Environment["ACTIVATION_TEST_PARENT_SKIP"] = "true";

            var result = await RunProcessAsync(startInfo);
            return (result.ExitCode, result.Output + result.Error);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            throw;
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string ExtractMarkedBlock(string script, string startMarker, string endMarker)
    {
        var start = script.IndexOf(startMarker, StringComparison.Ordinal);
        var contentStart = start < 0 ? -1 : start + startMarker.Length;
        var end = contentStart < 0 ? -1 : script.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > contentStart, "The required verifier test seam is missing.");
        return script[contentStart..end].Trim();
    }

    private static string ReadScript() => File.ReadAllText(ScriptPath());

    private static string ScriptPath()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        return TestPathUtils.PathUnder(repositoryRoot, "Durable", "verify-packed-consumers.sh");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"appsurface-activation-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
