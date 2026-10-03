using System.Text;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Console;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class DurableDoctorInputTests
{
    private const string ConnectionSecret = "durable-doctor-connection-secret-sentinel";
    private const string MalformedArgumentSecret = "durable-doctor-argv-secret-sentinel";
    private const string DefaultConnection = "Host=localhost;Username=doctor;Password=" + ConnectionSecret;
    private static readonly Guid RuntimeEpoch = Guid.Parse("6bb1fcd1-0a72-47c0-84a8-8cc4b68817dc");

    [Fact]
    public async Task Create_resolves_selected_values_once_and_keeps_connection_custody_private()
    {
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var input = DurableDoctorInput.Create(
            " CUSTOM_CONNECTION ",
            "CUSTOM_EPOCH",
            workerId: null,
            staleAfter: null,
            timeout: "1.5s",
            format: "json",
            name =>
            {
                reads[name] = reads.GetValueOrDefault(name) + 1;
                return name switch
                {
                    "CUSTOM_CONNECTION" => DefaultConnection,
                    "CUSTOM_EPOCH" => RuntimeEpoch.ToString("D"),
                    _ => null,
                };
            });
        var service = new SpyDoctorService();

        await input.InspectAsync(service, CancellationToken.None);

        Assert.Equal(2, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Equal(DefaultConnection, service.ConnectionString);
        Assert.Equal(RuntimeEpoch, service.Request?.ConfiguredRuntimeEpoch);
        Assert.Equal("CUSTOM_CONNECTION", service.Request?.ConnectionEnvironmentName);
        Assert.Equal("CUSTOM_EPOCH", service.Request?.EpochEnvironmentName);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), service.Request?.Timeout);
        Assert.Equal("json", service.Request?.Format);
        Assert.DoesNotContain(ConnectionSecret, service.Request!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_does_not_fall_back_when_a_selected_value_is_missing_and_reads_equal_names_once()
    {
        var selectedNames = new List<string>();
        Assert.Throws<DurableDoctorInputException>(() => DurableDoctorInput.Create(
            "CUSTOM_CONNECTION",
            epochEnvironmentName: null,
            workerId: null,
            staleAfter: null,
            timeout: null,
            format: null,
            name =>
            {
                selectedNames.Add(name);
                return name == DurableDoctorInput.DefaultEpochEnvironmentName ? RuntimeEpoch.ToString("D") : null;
            }));
        Assert.Equal(["CUSTOM_CONNECTION", DurableDoctorInput.DefaultEpochEnvironmentName], selectedNames);

        var readCount = 0;
        Assert.Throws<DurableDoctorInputException>(() => DurableDoctorInput.Create(
            "SHARED",
            "SHARED",
            null,
            null,
            null,
            null,
            _ =>
            {
                readCount++;
                return "not-a-guid";
            }));
        Assert.Equal(1, readCount);
    }

    [Theory]
    [InlineData("Username=operator;Password=private")]
    [InlineData("Host= ;Username=operator")]
    [InlineData("Host=localhost;Port=65536;Username=operator")]
    [InlineData("Host=localhost;Port=invalid;Username=operator")]
    [InlineData("Host=localhost;UnsupportedDoctorSetting=private")]
    public void Create_rejects_unparseable_or_hostless_connection_settings_without_exposing_values(string connection)
    {
        var exception = Assert.Throws<DurableDoctorInputException>(() => CreateInput(connectionString: connection));
        Assert.DoesNotContain("private", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("operator", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_accepts_native_npgsql_multi_host_configuration_without_opening_a_connection()
    {
        var input = CreateInput("Host=primary,replica;Port=5432;Username=doctor;Password=" + ConnectionSecret);

        Assert.Equal(RuntimeEpoch, input.Request.ConfiguredRuntimeEpoch);
        Assert.DoesNotContain(ConnectionSecret, input.Request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1s", 10_000_000)]
    [InlineData("1.5s", 15_000_000)]
    [InlineData("1m", 600_000_000)]
    [InlineData("1h", 36_000_000_000)]
    [InlineData("0.0001ms", 1)]
    [InlineData("0.0000001s", 1)]
    public void ParseDuration_accepts_invariant_positive_decimal_values_with_exact_ticks(string token, long ticks)
    {
        Assert.Equal(TimeSpan.FromTicks(ticks), DurableDoctorInput.ParseDuration(token, TimeSpan.Zero, TimeSpan.MaxValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0s")]
    [InlineData("-1s")]
    [InlineData("+1s")]
    [InlineData("1e1s")]
    [InlineData("NaNs")]
    [InlineData("1S")]
    [InlineData("1 ms")]
    [InlineData("1.0.0s")]
    [InlineData("0.00009ms")]
    [InlineData("0.00000009s")]
    [InlineData("1.00000000000000000000000000001s")]
    public void ParseDuration_rejects_invalid_or_subtick_values(string token) =>
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration(token, TimeSpan.Zero, TimeSpan.MaxValue));

    [Theory]
    [InlineData(".5s")]
    [InlineData("1.s")]
    public void ParseDuration_requires_digits_on_both_sides_of_a_decimal_point(string token) =>
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration(token, TimeSpan.Zero, TimeSpan.MaxValue));

    [Fact]
    public void ParseDuration_rejects_oversized_and_overflowing_tokens()
    {
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration(new string('9', 64) + "h", TimeSpan.Zero, TimeSpan.MaxValue));
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration(new string('1', 65) + "s", TimeSpan.Zero, TimeSpan.MaxValue));
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration("1s", TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(1)));
        Assert.Throws<DurableDoctorInputException>(() =>
            DurableDoctorInput.ParseDuration("1s", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Create_enforces_timeout_and_optional_worker_threshold_ranges_and_pairing()
    {
        var minimums = CreateInput(timeout: "1s", workerId: "worker-1", staleAfter: "1s");
        var maximums = CreateInput(timeout: "120s", workerId: "worker-1", staleAfter: "3600s");
        Assert.Equal(TimeSpan.FromSeconds(1), minimums.Request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), minimums.Request.StaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(2), maximums.Request.Timeout);
        Assert.Equal(TimeSpan.FromHours(1), maximums.Request.StaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(10), CreateInput().Request.Timeout);

        foreach (var (worker, stale, timeout) in new (string?, string?, string?)[]
        {
            ("worker-1", null, null),
            (null, "1s", null),
            ("worker-1", "999ms", null),
            ("worker-1", "3600.0000001s", null),
            (null, null, "999ms"),
            (null, null, "120.0000001s"),
        })
        {
            Assert.Throws<DurableDoctorInputException>(() => CreateInput(workerId: worker, staleAfter: stale, timeout: timeout));
        }
    }

    [Fact]
    public void Create_enforces_environment_name_and_worker_identifier_grammars()
    {
        Assert.Equal("_É9", DurableDoctorInput.ResolveEnvironmentName(" _É9 ", "DEFAULT"));
        Assert.Equal(new string('A', 200), DurableDoctorInput.ResolveEnvironmentName(new string('A', 200), "DEFAULT"));
        Assert.Equal("DEFAULT", DurableDoctorInput.ResolveEnvironmentName(null, "DEFAULT"));
        foreach (var name in new[] { "   ", "9NAME", "A-B", new string('A', 201) })
        {
            Assert.Throws<DurableDoctorInputException>(() => DurableDoctorInput.ResolveEnvironmentName(name, "DEFAULT"));
        }

        Assert.Equal(new string('a', 200), CreateInput(workerId: new string('a', 200), staleAfter: "1s").Request.WorkerId);
        Assert.Equal("AZaz09-_.:", CreateInput(workerId: "AZaz09-_.:", staleAfter: "1s").Request.WorkerId);
        foreach (var worker in new[] { "", " worker", "worker ", "worker/name", "wörker", new string('a', 201) })
        {
            Assert.Throws<DurableDoctorInputException>(() => CreateInput(workerId: worker, staleAfter: "1s"));
        }
    }

    [Fact]
    public void Create_accepts_only_exact_output_formats()
    {
        Assert.Equal("text", CreateInput().Request.Format);
        Assert.Equal("json", CreateInput(format: "json").Request.Format);
        Assert.Equal("text", CreateInput(format: "text").Request.Format);
        foreach (var format in new[] { "JSON", " text", "json ", "yaml", "" })
        {
            Assert.Throws<DurableDoctorInputException>(() => CreateInput(format: format));
        }
    }

    [Fact]
    public void Admission_accepts_only_known_option_tokens_and_safe_single_help_forms()
    {
        var joined = DurableDoctorArgumentAdmission.Inspect(
            ["durable", "doctor", "--connection-env=CONN", "--runtime-epoch-env=EPOCH", "--timeout=1.5s", "--format=json"]);
        Assert.True(joined.IsDoctor);
        Assert.False(joined.IsMalformed);
        Assert.Equal("json", joined.Format);

        var separated = DurableDoctorArgumentAdmission.Inspect(
            ["DURABLE", "DOCTOR", "--connection-env", "CONN", "--runtime-epoch-env", "EPOCH", "--timeout", "1s", "--format", "text"]);
        Assert.True(separated.IsDoctor);
        Assert.False(separated.IsMalformed);
        Assert.Equal("text", separated.Format);

        foreach (var help in new[] { "--help", "-h" })
        {
            var result = DurableDoctorArgumentAdmission.Inspect(["durable", "doctor", help]);
            Assert.True(result.IsHelp);
            Assert.False(result.IsMalformed);
        }
        Assert.False(DurableDoctorArgumentAdmission.Inspect(["durable", "schema"]).IsDoctor);

        var nonDoctorArguments = new[] { "durable", "schema", "--format=json" };
        Assert.Same(nonDoctorArguments, DurableDoctorArgumentAdmission.NormalizeJoinedValueOptions(nonDoctorArguments));
        Assert.Equal(
            ["durable doctor", "--timeout", "1.5s"],
            DurableDoctorArgumentAdmission.NormalizeJoinedValueOptions(["durable doctor", "--timeout=1.5s"]));
    }

    [Fact]
    public void Admission_rejects_ambiguous_or_option_like_values_without_mistaking_worker_text_for_format()
    {
        var malformedArguments = new string[][]
        {
            ["--unknown=secret"],
            ["--worker-id"],
            ["--worker-id", "--format=json"],
            ["--format=text", "--format", "json"],
            ["--format=json", "--format=text"],
            ["--help", "--format=json"],
            ["--help=secret"],
            ["--", "--format", "json"],
            ["unexpected"],
            ["--timeout=1s", "--timeout=2s"],
            ["--format="],
            ["--format"],
        };

        foreach (var tail in malformedArguments)
        {
            var decision = DurableDoctorArgumentAdmission.Inspect(["durable", "doctor", .. tail]);
            Assert.True(decision.IsMalformed, string.Join(' ', tail));
        }

        var embeddedFormat = DurableDoctorArgumentAdmission.Inspect(
            ["durable", "doctor", "--worker-id=--format=json"]);
        Assert.True(embeddedFormat.IsMalformed);
        Assert.Equal("text", embeddedFormat.Format);

        var invalidFormatValue = DurableDoctorArgumentAdmission.Inspect(
            ["durable", "doctor", "--format=invalid"]);
        Assert.False(invalidFormatValue.IsMalformed);
        Assert.Equal("text", invalidFormatValue.Format);

        var uniqueFormatAfterMissingValue = DurableDoctorArgumentAdmission.Inspect(
            ["durable", "doctor", "--worker-id", "--format=json"]);
        Assert.Equal("json", uniqueFormatAfterMissingValue.Format);
    }

    [Fact]
    public async Task Real_entrypoint_admits_doctor_before_generic_parser_and_keeps_malformed_tokens_private()
    {
        using var console = new FakeInMemoryConsole();
        var service = new SpyDoctorService();

        var run = await RunEntryPointAsync(
            ["durable", "doctor", "--format=json", "--unknown=" + MalformedArgumentSecret],
            console,
            service);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(string.Empty, run.RawOutput);
        Assert.Equal(string.Empty, run.RawError);
        Assert.Equal(0, service.CallCount);
        Assert.EndsWith("}\n", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(MalformedArgumentSecret, run.Output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(run.Output);
        Assert.Equal("invalid-input", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Real_entrypoint_converts_a_failing_configured_admission_sink_to_exit_one()
    {
        using var output = new ThrowingWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        var run = await RunEntryPointAsync(
            ["durable", "doctor", "--unknown=" + MalformedArgumentSecret],
            console,
            readStreams: static () => (string.Empty, string.Empty));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, output.WriteCount);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(string.Empty, run.RawOutput);
        Assert.Equal(string.Empty, run.RawError);
    }

    [Fact]
    public async Task Real_entrypoint_rejects_missing_connection_host_before_service_invocation()
    {
        var invalidConnection = "Username=doctor;Password=" + ConnectionSecret;
        using var connectionEnvironment = new EnvironmentVariableScope(
            DurableDoctorInput.DefaultConnectionEnvironmentName,
            invalidConnection);
        using var epochEnvironment = new EnvironmentVariableScope(
            DurableDoctorInput.DefaultEpochEnvironmentName,
            RuntimeEpoch.ToString("D"));
        using var console = new FakeInMemoryConsole();
        var service = new SpyDoctorService();

        var run = await RunEntryPointAsync(["durable", "doctor", "--format=json"], console, service);

        Assert.Equal(0, service.CallCount);
        Assert.DoesNotContain(ConnectionSecret, run.Output + run.Error + run.RawOutput + run.RawError, StringComparison.Ordinal);
        Assert.True(run.ExitCode == 3, $"Expected invalid-input exit 3, got {run.ExitCode}. Output: {run.Output}; error: {run.Error}; raw error: {run.RawError}");
        using var document = JsonDocument.Parse(run.Output);
        Assert.Equal("invalid-input", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Real_entrypoint_binds_joined_and_separated_values_and_honors_custom_service_registration()
    {
        using var connectionEnvironment = new EnvironmentVariableScope("CUSTOM_DOCTOR_CONNECTION", DefaultConnection);
        using var epochEnvironment = new EnvironmentVariableScope("CUSTOM_DOCTOR_EPOCH", RuntimeEpoch.ToString("D"));
        using var console = new FakeInMemoryConsole();
        var service = new SpyDoctorService(new DurableDoctorFailureException(DurableDoctorFailureKind.Unavailable, "dependency"));

        var run = await RunEntryPointAsync(
            [
                "durable", "doctor",
                "--connection-env=CUSTOM_DOCTOR_CONNECTION",
                "--runtime-epoch-env", "CUSTOM_DOCTOR_EPOCH",
                "--timeout=1.5s",
                "--format=json",
            ],
            console,
            service);

        Assert.DoesNotContain(ConnectionSecret, run.Output + run.Error + run.RawOutput + run.RawError, StringComparison.Ordinal);
        Assert.True(run.ExitCode == 4, $"Expected unavailable exit 4, got {run.ExitCode}. Service calls: {service.CallCount}. Output: {run.Output}; error: {run.Error}; raw error: {run.RawError}");
        Assert.Equal(1, service.CallCount);
        Assert.Equal(DefaultConnection, service.ConnectionString);
        Assert.Equal(RuntimeEpoch, service.Request?.ConfiguredRuntimeEpoch);
        Assert.Equal("CUSTOM_DOCTOR_CONNECTION", service.Request?.ConnectionEnvironmentName);
        Assert.Equal("CUSTOM_DOCTOR_EPOCH", service.Request?.EpochEnvironmentName);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), service.Request?.Timeout);
        Assert.DoesNotContain(ConnectionSecret, service.Request!.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(string.Empty, run.RawError);
        using var document = JsonDocument.Parse(run.Output);
        Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Real_entrypoint_help_discovers_doctor_without_resolving_environment_or_inspecting_store()
    {
        using var console = new FakeInMemoryConsole();
        var service = new SpyDoctorService();

        var run = await RunEntryPointAsync(["durable", "doctor", "--help"], console, service);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, service.CallCount);
        Assert.Contains("durable doctor", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--connection-env", run.Output, StringComparison.Ordinal);
        Assert.Contains("--runtime-epoch-env", run.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(string.Empty, run.RawError);
    }

    [Fact]
    public async Task Real_entrypoint_durable_root_help_mentions_read_only_doctor()
    {
        using var console = new FakeInMemoryConsole();

        var run = await RunEntryPointAsync(["durable"], console);

        Assert.Contains("appsurface durable doctor", run.Output, StringComparison.Ordinal);
        Assert.Contains("read-only store/runtime checks", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_entrypoint_single_argv_multiword_route_does_not_leak_following_option_values()
    {
        using var console = new FakeInMemoryConsole();

        var run = await RunEntryPointAsync(
            ["durable doctor", "--unknown=" + MalformedArgumentSecret],
            console);

        Assert.Equal(3, run.ExitCode);
        Assert.DoesNotContain(MalformedArgumentSecret, run.Output + run.Error + run.RawOutput + run.RawError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_entrypoint_cancellation_after_async_inspection_returns_only_the_fixed_canceled_result()
    {
        using var connectionEnvironment = new EnvironmentVariableScope(
            DurableDoctorInput.DefaultConnectionEnvironmentName,
            DefaultConnection);
        using var epochEnvironment = new EnvironmentVariableScope(
            DurableDoctorInput.DefaultEpochEnvironmentName,
            RuntimeEpoch.ToString("D"));
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, output, error);
        var service = new WaitingDoctorService();

        var runTask = RunEntryPointAsync(
            ["durable", "doctor", "--format=json"],
            console,
            service,
            () => (ReadStream(output), ReadStream(error)));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        console.RequestCancellation();
        var run = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(1, service.CallCount);
        Assert.Equal(string.Empty, run.Error);
        Assert.Equal(string.Empty, run.RawError);
        Assert.DoesNotContain("OperationCanceledException", run.RawOutput + run.RawError, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(run.Output);
        Assert.Equal("canceled", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Render_contract_rejection_writes_one_fixed_failed_envelope_and_sets_exit_one()
    {
        using var writer = new CountingTextWriter();
        var invalidResult = new DurableDoctorResult(
            "passed",
            0,
            [],
            observedAtUtc: null,
            schema: null,
            storeId: null,
            configuredRuntimeEpoch: null,
            activeRuntimeEpoch: null,
            credential: null,
            retention: null,
            worker: null,
            findings: [],
            nextAction: new DurableDoctorAction(
                "invalid",
                null,
                [],
                new Uri("https://example.test/doctor", UriKind.Absolute)));
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await DurableDoctorCommand.WriteResultAsync(writer, invalidResult, null, "json");
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
        Assert.Equal(1, writer.WriteCount);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("ASDUR415", document.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Broken_output_sink_is_attempted_once_and_returns_exit_one_without_retry()
    {
        using var writer = new ThrowingTextWriter();
        var result = DurableDoctorClassifier.Terminal(null, "failed", ["catalog-contract"]);
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await DurableDoctorCommand.WriteResultAsync(writer, result, null, "json");
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }

        Assert.Equal(1, writer.WriteCount);
    }

    private static DurableDoctorInput CreateInput(
        string? connectionString = null,
        string? epoch = null,
        string? workerId = null,
        string? staleAfter = null,
        string? timeout = null,
        string? format = null,
        string? connectionName = null,
        string? epochName = null)
    {
        var selectedConnectionName = connectionName ?? DurableDoctorInput.DefaultConnectionEnvironmentName;
        var selectedEpochName = epochName ?? DurableDoctorInput.DefaultEpochEnvironmentName;
        return DurableDoctorInput.Create(
            connectionName,
            epochName,
            workerId,
            staleAfter,
            timeout,
            format,
            name => name switch
            {
                var value when value == selectedConnectionName => connectionString ?? DefaultConnection,
                var value when value == selectedEpochName => epoch ?? RuntimeEpoch.ToString("D"),
                _ => null,
            });
    }

    private static async Task<CapturedRun> RunEntryPointAsync(
        string[] arguments,
        IConsole console,
        IDurableDoctorService? doctorService = null,
        Func<(string Output, string Error)>? readStreams = null)
    {
        var originalExitCode = Environment.ExitCode;
        var originalOutput = System.Console.Out;
        var originalError = System.Console.Error;
        using var rawOutput = new StringWriter();
        using var rawError = new StringWriter();
        try
        {
            Environment.ExitCode = 0;
            System.Console.SetOut(rawOutput);
            System.Console.SetError(rawError);
            await ProgramEntryPoint.RunAsync(arguments, options =>
            {
                options.CustomRegistrations.Add(services => services.AddSingleton(console));
                if (doctorService is not null)
                {
                    options.CustomRegistrations.Add(services => services.AddSingleton(doctorService));
                }
            });
            await console.Output.FlushAsync();
            var capturedStreams = readStreams is not null
                ? readStreams()
                : console is FakeInMemoryConsole inMemory
                    ? (inMemory.ReadOutputString(), inMemory.ReadErrorString())
                    : throw new InvalidOperationException("A stream capture function is required for this test console.");
            return new CapturedRun(
                capturedStreams.Item1,
                capturedStreams.Item2,
                rawOutput.ToString(),
                rawError.ToString(),
                Environment.ExitCode);
        }
        finally
        {
            System.Console.SetOut(originalOutput);
            System.Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
        }
    }

    private static string ReadStream(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed record CapturedRun(string Output, string Error, string RawOutput, string RawError, int ExitCode);

    private sealed class SpyDoctorService(Exception? failure = null) : IDurableDoctorService
    {
        internal int CallCount { get; private set; }
        internal string? ConnectionString { get; private set; }
        internal DurableDoctorRequest? Request { get; private set; }

        public ValueTask<DurableDoctorObservation> InspectAsync(
            string connectionString,
            DurableDoctorRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ConnectionString = connectionString;
            Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            return failure is null
                ? ValueTask.FromResult(new DurableDoctorObservation([]))
                : ValueTask.FromException<DurableDoctorObservation>(failure);
        }
    }

    private sealed class WaitingDoctorService : IDurableDoctorService
    {
        private int _callCount;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<DurableDoctorObservation> InspectAsync(
            string connectionString,
            DurableDoctorRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new DurableDoctorObservation([]);
        }
    }

    private sealed class CountingTextWriter : StringWriter
    {
        internal int WriteCount { get; private set; }

        public override Task WriteAsync(string? value)
        {
            WriteCount++;
            return base.WriteAsync(value);
        }
    }

    private sealed class ThrowingTextWriter : StringWriter
    {
        internal int WriteCount { get; private set; }

        public override Task WriteAsync(string? value)
        {
            WriteCount++;
            return Task.FromException(new IOException("private sink failure"));
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _writeCount);
            throw new IOException("private sink failure");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            return ValueTask.FromException(new IOException("private sink failure"));
        }
    }
}
