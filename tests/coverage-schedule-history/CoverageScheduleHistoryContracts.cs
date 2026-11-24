using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Offline contracts for the coverage schedule history restore command.</summary>
internal static class CoverageScheduleHistoryContracts
{
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private const int MaxZipBytes = MaxJsonBytes + 64 * 1024;
    private const int MaxProjects = 1000;
    private const int MaxSeconds = int.MaxValue;
    private const string Repository = "forge-trust/AppSurface";
    private const string Branch = "main";
    private const string ArtifactName = "coverage-schedule-timings";
    private const string CodecPayloadSha256 = "fc14993b23c567ea4289d207bfdc080cbd4936aaad2df10a39d8e1257d7fb104";
    private const string Bzip2ArchiveSha256 = "fc68f7bdfa1f6deeed4e7a6c19f9e254b500e09237feec52175805eacfa2b34a";
    private const string LzmaArchiveSha256 = "ed599f1f15fcb18cfa41383db9764b6fd9be5467a7ebf1642c5e899a42a6ce86";
    private const string Method9ArchiveSha256 = "799a7b10c62571dbacb5980bd054d432b474810ce998b49127e59b5b130a3034";
    private const string CodecPayloadBase64 = "eyAicHJvamVjdHMiOiBbIHsicHJvamVjdCI6InNyYy9BcHAvQXBwLmNzcHJvaiIsICJzZWNvbmRzIjo4NH0gXSB9Cg==";

    private static readonly string RunsEndpoint = RunsEndpointFor(Repository, Branch);
    private static readonly string Root = FindRepositoryRoot();

    private sealed record RequestTrace(string Endpoint, int MaxBytes, TimeSpan Timeout);
    private sealed record RunResult(int ExitCode, string StandardOutput, string StandardError, IReadOnlyList<RequestTrace> Requests);
    private sealed record ZipEntry(string Name, byte[] Bytes, CompressionLevel Compression = CompressionLevel.Optimal);
    private sealed record Case(string Name, Func<Task> Run);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("coverage-history-contract-").FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private readonly DateTimeOffset _origin = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _origin.AddTicks(_timestamp);

        public void Advance(TimeSpan amount) => _timestamp = checked(_timestamp + amount.Ticks);
    }

    public static async Task<int> RunAsync()
    {
        var cases = new Case[]
        {
            new("test_restores_only_valid_exact_nonexpired_artifact", ExactNonExpiredArtifactAsync),
            new("test_no_history_and_api_failure_succeed_without_creating_output_or_parent", NoHistoryAndApiFailureAsync),
            new("test_only_default_branch_completed_successes_are_eligible", EligibleRunsAndOrderingAsync),
            new("test_only_push_runs_from_the_current_repository_are_eligible", PushAndRepositoryEligibilityAsync),
            new("test_missing_or_null_run_event_and_repository_shapes_are_ineligible", MissingRunFieldsAsync),
            new("test_missing_or_null_workflow_runs_shape_falls_back_without_output", InvalidWorkflowRunsShapeAsync),
            new("test_invalid_json_oversize_entry_duplicate_entries_and_duplicate_projects_are_ignored", InvalidContentsAsync),
            new("test_falls_back_to_previous_valid_artifact", OlderValidFallbackAsync),
            new("test_invalid_artifact_size_metadata_is_skipped_before_download", InvalidArtifactMetadataAsync),
            new("test_unsupported_compression_and_corrupt_deflate_fall_back_without_output", UnreadableMemberAsync),
            new("test_total_api_deadline_bounds_each_request", SharedDeadlineAsync),
            new("test_api_failure_does_not_fall_back_or_create_output", ApiFailureStopsSearchAsync),
            new("test_existing_output_is_never_clobbered_and_is_rejected_before_api_call", ExistingOutputAsync),
            new("test_required_configuration_and_absolute_output_are_fatal", BadInvocationAsync),
            new("zip_codec_acceptance_and_decodable_method_9_rejection", CodecAcceptanceAsync),
            new("timing_json_and_archive_boundaries", InputBoundariesAsync),
            new("exclusive_output_ownership_and_partial_write_cleanup", OutputOwnershipAsync),
            new("gh_process_runner", GhProcessContracts.RunAsync)
        };

        var failures = 0;
        foreach (var testCase in cases)
        {
            try
            {
                await testCase.Run();
                Console.WriteLine($"PASS {testCase.Name}");
            }
            catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {testCase.Name}: {error}");
            }
        }

        Console.WriteLine($"Coverage schedule history contracts: {cases.Length - failures}/{cases.Length} passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task ExactNonExpiredArtifactAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var output = Path.Combine(temporary.Path, "created", "timings.json");
        var fixture = ReadCanonicalFixture();
        var archive = MakeZip(
            new("nested/timings.json", Encoding.UTF8.GetBytes("{\"projects\":[]}")),
            new("notes.txt", Encoding.UTF8.GetBytes("ignored")),
            new("timings.json", fixture));
        var responses = new Dictionary<string, byte[]>
        {
            [RunsEndpoint] = Runs(RunRecord(40)),
            [ArtifactsEndpoint(40)] = Artifacts(
                Artifact(91, expired: true),
                Artifact(92, name: ArtifactName + "-extra"),
                Artifact(93)),
            [DownloadEndpoint(93)] = archive
        };

        var result = await InvokeAsync(temporary.Path, output, responses);

        Equal(0, result.ExitCode, "exit code");
        Equal(string.Empty, result.StandardError, "stderr");
        Equal(string.Empty, result.StandardOutput, "stdout");
        BytesEqual(fixture, File.ReadAllBytes(output), "restored original timings bytes");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(40), DownloadEndpoint(93)],
            Endpoints(result),
            "request order");
        SequenceEqual(
            [MaxJsonBytes, MaxJsonBytes, MaxZipBytes],
            result.Requests.Select(request => request.MaxBytes),
            "request byte limits");
        SequenceEqual(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            result.Requests.Select(request => request.Timeout),
            "initial request timeouts");
        Check(Directory.Exists(Path.GetDirectoryName(output)!), "The output parent was not created after a valid artifact.");
        AssertCanonicalFixtureShape(fixture);
    }

    private static async Task NoHistoryAndApiFailureAsync()
    {
        var cases = new (string Name, Func<byte[]> Response, string Warning, bool Throws)[]
        {
            ("empty history", () => Runs(), "no successful completed build runs", false),
            ("gh authentication failure", () => [], "GitHub workflow history is unavailable", true),
            ("integer exceeds JSON digit limit", () => Encoding.UTF8.GetBytes(
                "{\"workflow_runs\":" + new string('1', 5000) + "}"), "GitHub workflow history is unavailable", false),
            ("oversized history JSON", () => new byte[MaxJsonBytes + 1], "GitHub workflow history is unavailable", false),
            ("invalid history JSON", () => Encoding.UTF8.GetBytes("{broken"), "GitHub workflow history is unavailable", false)
        };

        foreach (var testCase in cases)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, "coverage-schedule", "timings.json");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                new Dictionary<string, byte[]>(),
                responder: request =>
                {
                    Equal(RunsEndpoint, request.Endpoint, testCase.Name + " request endpoint");
                    if (testCase.Throws)
                    {
                        throw new IOException("simulated gh authentication failure");
                    }
                    return Task.FromResult(testCase.Response());
                });

            Equal(0, result.ExitCode, testCase.Name + " exit code");
            Contains(testCase.Warning, result.StandardError, testCase.Name + " warning");
            SequenceEqual([RunsEndpoint], Endpoints(result), testCase.Name + " request trace");
            Check(!PathExists(output), testCase.Name + " created an output file.");
            Check(!Directory.Exists(Path.GetDirectoryName(output)!), testCase.Name + " created its output parent.");
        }
    }

    private static async Task EligibleRunsAndOrderingAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var records = new[]
        {
            RunRecord(99, branch: "feature", createdAt: "2026-11-21T12:00:00Z"),
            RunRecord(98, status: "in_progress", createdAt: "2026-11-20T12:00:00Z"),
            RunRecord(97, conclusion: "failure", createdAt: "2026-11-19T12:00:00Z"),
            RunRecord("96", createdAt: "2026-11-18T12:00:00Z"),
            RunRecord(95, createdAt: "not-a-date"),
            RunRecord(94, createdAt: "2026-11-17T12:00:00"),
            RunRecord(4, createdAt: "2026-11-10T12:00:00Z"),
            RunRecord(7, createdAt: "2026-11-10T12:00:00Z"),
            RunRecord(8, createdAt: "2026-11-11T12:00:00Z")
        };
        var responses = new Dictionary<string, byte[]>
        {
            [RunsEndpoint] = Runs(records),
            [ArtifactsEndpoint(8)] = Artifacts(),
            [ArtifactsEndpoint(7)] = Artifacts(),
            [ArtifactsEndpoint(4)] = Artifacts()
        };

        var result = await InvokeAsync(temporary.Path, Path.Combine(temporary.Path, "none", "timings.json"), responses);

        Equal(0, result.ExitCode, "exit code");
        Contains("no valid coverage schedule timings artifact", result.StandardError, "exhausted-history warning");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(8), ArtifactsEndpoint(7), ArtifactsEndpoint(4)],
            Endpoints(result),
            "eligibility and timestamp/run-id ordering");
        Check(!Directory.Exists(Path.Combine(temporary.Path, "none")), "No-output history created its parent.");
    }

    private static async Task PushAndRepositoryEligibilityAsync()
    {
        using var temporary = NewTemporaryDirectory();
        const string branch = "release/β branch";
        const string repository = "forge-trust/AppSurface";
        var runsEndpoint = RunsEndpointFor(repository, branch);
        var result = await InvokeAsync(
            temporary.Path,
            Path.Combine(temporary.Path, "none", "timings.json"),
            new Dictionary<string, byte[]>
            {
                [runsEndpoint] = Runs(
                    RunRecord(90, branch: branch, eventName: "pull_request", headRepository: "fork-owner/AppSurface"),
                    RunRecord(89, branch: branch, eventName: "workflow_dispatch"),
                    RunRecord(88, branch: branch, headRepository: "another-owner/AppSurface"),
                    RunRecord(1, branch: branch, headRepository: "FORGE-TRUST/APPSURFACE")),
                [ArtifactsEndpoint(1)] = Artifacts()
            },
            environment: DefaultEnvironment(repository, branch));

        Equal(0, result.ExitCode, "exit code");
        SequenceEqual(
            [runsEndpoint, ArtifactsEndpoint(1)],
            Endpoints(result),
            "push and case-insensitive repository eligibility");
        Contains("branch=release%2F%CE%B2+branch", runsEndpoint, "form-encoded branch query");
    }

    private static async Task MissingRunFieldsAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var records = new[]
        {
            RunRecord(90, includeEvent: false),
            RunRecord(89, eventName: null),
            RunRecord(88, includeHeadRepository: false),
            RunRecord(87, nullHeadRepository: true),
            RunRecord(86, headRepository: new Dictionary<string, object?> { ["full_name"] = null }),
            RunRecord(85, headRepository: new Dictionary<string, object?>()),
            RunRecord(1)
        };
        var result = await InvokeAsync(
            temporary.Path,
            Path.Combine(temporary.Path, "none", "timings.json"),
            new Dictionary<string, byte[]>
            {
                [RunsEndpoint] = Runs(records),
                [ArtifactsEndpoint(1)] = Artifacts()
            });

        Equal(0, result.ExitCode, "exit code");
        SequenceEqual([RunsEndpoint, ArtifactsEndpoint(1)], Endpoints(result), "required run fields");
    }

    private static async Task InvalidWorkflowRunsShapeAsync()
    {
        var payloads = new[]
        {
            Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes("{\"workflow_runs\":null}"),
            Encoding.UTF8.GetBytes("{\"workflow_runs\":42}")
        };

        for (var index = 0; index < payloads.Length; index++)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, $"invalid-shape-{index}", "timings.json");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                new Dictionary<string, byte[]> { [RunsEndpoint] = payloads[index] });

            Equal(0, result.ExitCode, $"shape {index} exit code");
            Contains("GitHub workflow history is unavailable", result.StandardError, $"shape {index} warning");
            SequenceEqual([RunsEndpoint], Endpoints(result), $"shape {index} request trace");
            Check(!PathExists(output), $"Malformed workflow_runs shape {index} created output.");
            Check(!Directory.Exists(Path.GetDirectoryName(output)!), $"Malformed workflow_runs shape {index} created parent.");
        }
    }

    private static async Task InvalidContentsAsync()
    {
        var duplicateTimingKey = Encoding.UTF8.GetBytes(
            "{\"projects\":[{\"project\":\"src/App/App.csproj\",\"project\":\"other.csproj\",\"seconds\":1}]}");
        var duplicateRootEntries = MakeZip(
            new("timings.json", ReadCanonicalFixture()),
            new ZipEntry("timings.json", TimingBytes("src/Other/Other.csproj", 2)));
        var tooManyProjects = Enumerable.Range(0, MaxProjects + 1)
            .Select(index => new Dictionary<string, object?>
            {
                ["project"] = $"src/Project{index}/Project{index}.csproj",
                ["seconds"] = 1
            })
            .ToArray();
        var cases = new (string Name, byte[] Archive)[]
        {
            ("invalid-json", MakeZip(new("timings.json", Encoding.UTF8.GetBytes("not JSON")))),
            ("oversized-integer", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":" + new string('1', 5000) + "}]}")))),
            ("oversized-timings-member", MakeZip(new("timings.json", new byte[MaxJsonBytes + 1]))),
            ("oversized-unrelated-member", MakeZip(new("timings.json", ReadCanonicalFixture()), new ZipEntry("notes.bin", new byte[MaxJsonBytes + 1]))),
            ("duplicate-root-entry", duplicateRootEntries),
            ("duplicate-project", MakeZip(new("timings.json", TimingBytes(
                ("src/App/App.csproj", 1), ("src/App/App.csproj", 2))))),
            ("case-insensitive-duplicate-project", MakeZip(new("timings.json", TimingBytes(
                ("src/App/App.csproj", 1), ("SRC/app/app.csproj", 2))))),
            ("padded-project-path", MakeZip(new("timings.json", TimingBytes(" src/App/App.csproj", 1)))),
            ("empty-projects", MakeZip(new("timings.json", Encoding.UTF8.GetBytes("{\"projects\":[]}")))),
            ("duplicate-json-key", MakeZip(new("timings.json", duplicateTimingKey))),
            ("escaped-duplicate-key", MakeZip(new("timings.json", """
                {"projects":[{"project":"a.csproj","seconds":1}], "a":1, "\u0061" :2}
                """u8.ToArray()))),
            ("escaped-surrogate-duplicate-key", MakeZip(new("timings.json", """
                {"projects":[{"project":"a.csproj","seconds":1}], "\uD800":1, "\ud800" :2}
                """u8.ToArray()))),
            ("duplicate-extra-json-key", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"extra\":{\"key\":1,\"key\":2},\"projects\":[{\"project\":\"a.csproj\",\"seconds\":1}]}")))),
            ("unsafe-project", MakeZip(new("timings.json", TimingBytes("../App.csproj", 1)))),
            ("project-path-over-code-point-limit", MakeZip(new("timings.json", TimingBytes(
                string.Concat(Enumerable.Repeat("😀", 4090)) + ".csproj", 1)))),
            ("boolean-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":true}]}")))),
            ("null-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":null}]}")))),
            ("nan-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":NaN}]}")))),
            ("positive-infinity-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":Infinity}]}")))),
            ("negative-infinity-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":-Infinity}]}")))),
            ("fraction-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":1.0}]}")))),
            ("exponent-seconds", MakeZip(new("timings.json", Encoding.UTF8.GetBytes(
                "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":1e2}]}")))),
            ("negative-seconds", MakeZip(new("timings.json", TimingBytes("a.csproj", -1)))),
            ("seconds-over-int32", MakeZip(new("timings.json", TimingBytes("a.csproj", (long)int.MaxValue + 1)))),
            ("too-many-projects", MakeZip(new("timings.json", JsonSerializer.SerializeToUtf8Bytes(new { projects = tooManyProjects })))),
            ("invalid-utf8", MakeZip(new("timings.json", [0x7b, 0x22, 0xff, 0x22, 0x3a, 0x31, 0x7d]))),
            ("utf8-bom", MakeZip(new("timings.json", [0xef, 0xbb, 0xbf, .. TimingBytes("a.csproj", 1)])))
        };

        foreach (var (name, archive) in cases)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, $"invalid-{name}", "timings.json");
            var responses = SingleCandidateResponses(archive);
            var result = await InvokeAsync(temporary.Path, output, responses);

            Equal(0, result.ExitCode, name + " exit code");
            Contains("warning:", result.StandardError, name + " warning");
            SequenceEqual(
                [RunsEndpoint, ArtifactsEndpoint(8), DownloadEndpoint(18)],
                Endpoints(result),
                name + " request trace");
            Check(!PathExists(output), name + " created output.");
            Check(!Directory.Exists(Path.GetDirectoryName(output)!), name + " created output parent.");
        }
    }

    private static async Task OlderValidFallbackAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var fixture = ReadCanonicalFixture();
        var invalidNewer = MakeZip(new("timings.json", Encoding.UTF8.GetBytes("{\"projects\":[]}")));
        var validOlder = MakeZip(new("timings.json", fixture));
        var result = await InvokeAsync(
            temporary.Path,
            Path.Combine(temporary.Path, "fallback", "timings.json"),
            new Dictionary<string, byte[]>
            {
                [RunsEndpoint] = Runs(
                    RunRecord(20, createdAt: "2026-11-10T12:00:00Z"),
                    RunRecord(19, createdAt: "2026-11-09T12:00:00Z")),
                [ArtifactsEndpoint(20)] = Artifacts(Artifact(120)),
                [DownloadEndpoint(120)] = invalidNewer,
                [ArtifactsEndpoint(19)] = Artifacts(Artifact(119)),
                [DownloadEndpoint(119)] = validOlder
            });

        Equal(0, result.ExitCode, "exit code");
        Equal(string.Empty, result.StandardError, "stderr");
        BytesEqual(fixture, File.ReadAllBytes(Path.Combine(temporary.Path, "fallback", "timings.json")), "fallback bytes");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(20), DownloadEndpoint(120), ArtifactsEndpoint(19), DownloadEndpoint(119)],
            Endpoints(result),
            "newer-invalid then older-valid request trace");
    }

    private static async Task InvalidArtifactMetadataAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var fixture = ReadCanonicalFixture();
        var output = Path.Combine(temporary.Path, "size-fallback", "timings.json");
        var result = await InvokeAsync(
            temporary.Path,
            output,
            new Dictionary<string, byte[]>
            {
                [RunsEndpoint] = Runs(
                    RunRecord(20, createdAt: "2026-11-10T12:00:00Z"),
                    RunRecord(19, createdAt: "2026-11-09T12:00:00Z")),
                [ArtifactsEndpoint(20)] = Artifacts(
                    Artifact(120, size: "1024"),
                    Artifact(121, size: (long)MaxZipBytes + 1),
                    Artifact(122, size: -1),
                    Artifact("123"),
                    Artifact(0),
                    Artifact(124, size: MaxZipBytes)),
                [ArtifactsEndpoint(19)] = Artifacts(Artifact(119)),
                [DownloadEndpoint(124)] = MakeZip(new("timings.json", Encoding.UTF8.GetBytes("bad"))),
                [DownloadEndpoint(119)] = MakeZip(new("timings.json", fixture))
            });

        Equal(0, result.ExitCode, "exit code");
        Equal(string.Empty, result.StandardError, "stderr");
        BytesEqual(fixture, File.ReadAllBytes(output), "older valid bytes");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(20), DownloadEndpoint(124), ArtifactsEndpoint(19), DownloadEndpoint(119)],
            Endpoints(result),
            "invalid metadata must skip download");
        Check(!Endpoints(result).Contains(DownloadEndpoint(120)), "String size metadata was downloaded.");
        Check(!Endpoints(result).Contains(DownloadEndpoint(121)), "Oversized size metadata was downloaded.");
        Check(!Endpoints(result).Contains(DownloadEndpoint(122)), "Negative size metadata was downloaded.");
        Check(!Endpoints(result).Contains(DownloadEndpoint(123)), "String artifact id was downloaded.");
        Check(!Endpoints(result).Contains(DownloadEndpoint(0)), "Nonpositive artifact id was downloaded.");
    }

    private static async Task UnreadableMemberAsync()
    {
        var invalidArchives = new (string Name, byte[] Archive)[]
        {
            ("unsupported-compression", SetZipCompressionMethod(MakeZip(new("timings.json", ReadCanonicalFixture())), 99)),
            ("encrypted-flag", SetZipEncryptedFlag(MakeZip(new("timings.json", ReadCanonicalFixture())))),
            ("corrupt-deflate", CorruptDeflate(MakeZip(new("timings.json", ReadCanonicalFixture())))),
            ("corrupt-crc", CorruptStoredCrc(MakeZip(new("timings.json", ReadCanonicalFixture(), CompressionLevel.NoCompression))))
        };

        foreach (var (name, archive) in invalidArchives)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, $"invalid-zip-{name}", "timings.json");
            var result = await InvokeAsync(temporary.Path, output, SingleCandidateResponses(archive));

            Equal(0, result.ExitCode, name + " exit code");
            Contains("no valid coverage schedule timings artifact", result.StandardError, name + " warning");
            Equal(DownloadEndpoint(18), result.Requests[^1].Endpoint, name + " final request");
            Check(!PathExists(output), name + " created output.");
            Check(!Directory.Exists(Path.GetDirectoryName(output)!), name + " created output parent.");
        }
    }

    private static async Task SharedDeadlineAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var output = Path.Combine(temporary.Path, "deadline", "timings.json");
        var clock = new ManualTimeProvider();
        var result = await InvokeAsync(
            temporary.Path,
            output,
            new Dictionary<string, byte[]>(),
            clock: clock,
            responder: request =>
            {
                switch (request.Endpoint)
                {
                    case var endpoint when endpoint == RunsEndpoint:
                        clock.Advance(TimeSpan.FromSeconds(20));
                        return Task.FromResult(Runs(RunRecord(20)));
                    case var endpoint when endpoint == ArtifactsEndpoint(20):
                        clock.Advance(TimeSpan.FromSeconds(20));
                        return Task.FromResult(Artifacts(Artifact(120)));
                    case var endpoint when endpoint == DownloadEndpoint(120):
                        clock.Advance(request.Timeout);
                        throw new IOException("simulated request deadline");
                    default:
                        throw new InvalidOperationException($"Unexpected request {request.Endpoint}.");
                }
            });

        Equal(0, result.ExitCode, "exit code");
        Contains("GitHub artifact download is unavailable", result.StandardError, "deadline warning");
        SequenceEqual([30d, 25d, 5d], result.Requests.Select(request => request.Timeout.TotalSeconds), "bounded request timeouts");
        SequenceEqual([RunsEndpoint, ArtifactsEndpoint(20), DownloadEndpoint(120)], Endpoints(result), "deadline request trace");
        Check(!PathExists(output), "An exhausted deadline created output.");
        Check(!Directory.Exists(Path.GetDirectoryName(output)!), "An exhausted deadline created output parent.");
    }

    private static async Task ApiFailureStopsSearchAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var result = await InvokeAsync(
            temporary.Path,
            Path.Combine(temporary.Path, "api-failure", "timings.json"),
            new Dictionary<string, byte[]>
            {
                [RunsEndpoint] = Runs(
                    RunRecord(20, createdAt: "2026-11-10T12:00:00Z"),
                    RunRecord(19, createdAt: "2026-11-09T12:00:00Z"))
            },
            responder: request => request.Endpoint switch
            {
                var endpoint when endpoint == RunsEndpoint => Task.FromResult(Runs(
                    RunRecord(20, createdAt: "2026-11-10T12:00:00Z"),
                    RunRecord(19, createdAt: "2026-11-09T12:00:00Z"))),
                var endpoint when endpoint == ArtifactsEndpoint(20) => throw new IOException("simulated artifact list failure"),
                _ => throw new InvalidOperationException($"Unexpected request {request.Endpoint}.")
            });

        Equal(0, result.ExitCode, "exit code");
        Contains("GitHub artifact history is unavailable", result.StandardError, "warning");
        SequenceEqual([RunsEndpoint, ArtifactsEndpoint(20)], Endpoints(result), "API failure must stop fallback");
        Check(!Directory.Exists(Path.Combine(temporary.Path, "api-failure")), "API failure created output parent.");

        var invalidArtifactPayloads = new[]
        {
            Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes("{\"artifacts\":null}"),
            Encoding.UTF8.GetBytes("{\"artifacts\":42}")
        };
        foreach (var payload in invalidArtifactPayloads)
        {
            using var invalidTemporary = NewTemporaryDirectory();
            var invalidResult = await InvokeAsync(
                invalidTemporary.Path,
                Path.Combine(invalidTemporary.Path, "invalid-artifacts", "timings.json"),
                new Dictionary<string, byte[]>(),
                responder: request => request.Endpoint == RunsEndpoint
                    ? Task.FromResult(Runs(
                        RunRecord(20, createdAt: "2026-11-10T12:00:00Z"),
                        RunRecord(19, createdAt: "2026-11-09T12:00:00Z")))
                    : request.Endpoint == ArtifactsEndpoint(20)
                        ? Task.FromResult(payload)
                        : throw new InvalidOperationException($"Unexpected request {request.Endpoint}."));

            Equal(0, invalidResult.ExitCode, "invalid artifact shape exit code");
            Contains("GitHub artifact history is unavailable", invalidResult.StandardError, "invalid artifact shape warning");
            SequenceEqual(
                [RunsEndpoint, ArtifactsEndpoint(20)],
                Endpoints(invalidResult),
                "invalid artifact shape stops older-run fallback");
            Check(!Directory.Exists(Path.Combine(invalidTemporary.Path, "invalid-artifacts")), "Invalid artifact shape created output parent.");
        }
    }

    private static async Task ExistingOutputAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var output = Path.Combine(temporary.Path, "already-there.json");
        var original = Encoding.UTF8.GetBytes("keep these exact bytes\n");
        File.WriteAllBytes(output, original);
        var result = await InvokeAsync(
            temporary.Path,
            output,
            new Dictionary<string, byte[]>(),
            environment: new Dictionary<string, string?>());

        Equal(2, result.ExitCode, "exit code");
        Contains("already exists", result.StandardError, "occupied output diagnostic");
        BytesEqual(original, File.ReadAllBytes(output), "existing output preservation");
        Equal(0, result.Requests.Count, "request count for an occupied output");

        var directoryOutput = Path.Combine(temporary.Path, "occupied-directory");
        Directory.CreateDirectory(directoryOutput);
        var directoryResult = await InvokeAsync(
            temporary.Path,
            directoryOutput,
            new Dictionary<string, byte[]>(),
            environment: new Dictionary<string, string?>());
        Equal(2, directoryResult.ExitCode, "directory destination exit code");
        Equal(0, directoryResult.Requests.Count, "directory destination request count");
        Check(Directory.Exists(directoryOutput), "The occupied destination directory was removed.");

        var danglingLink = Path.Combine(temporary.Path, "dangling-link.json");
        File.CreateSymbolicLink(danglingLink, Path.Combine(temporary.Path, "missing-target"));
        var linkResult = await InvokeAsync(
            temporary.Path,
            danglingLink,
            new Dictionary<string, byte[]>(),
            environment: new Dictionary<string, string?>());
        Equal(2, linkResult.ExitCode, "dangling symlink exit code");
        Equal(0, linkResult.Requests.Count, "dangling symlink request count");
        Check(PathExists(danglingLink), "The dangling symlink was removed.");

        await OutputPathComponentsAsync();
    }

    private static async Task OutputPathComponentsAsync()
    {
        using (var temporary = NewTemporaryDirectory())
        {
            var symlinkTargetParent = Path.Combine(temporary.Path, "symlink-target");
            var symlinkTargetChild = Path.Combine(symlinkTargetParent, "child");
            Directory.CreateDirectory(symlinkTargetChild);
            var alias = Path.Combine(temporary.Path, "alias");
            Directory.CreateSymbolicLink(alias, symlinkTargetChild);
            var existingViaParentTraversal = Path.Combine(alias, "..", "already-there.json");
            var existingTarget = Path.Combine(symlinkTargetParent, "already-there.json");
            var original = Encoding.UTF8.GetBytes("preserve through symlink traversal\n");
            File.WriteAllBytes(existingTarget, original);

            var result = await InvokeAsync(
                temporary.Path,
                existingViaParentTraversal,
                new Dictionary<string, byte[]>(),
                environment: new Dictionary<string, string?>());

            Equal(2, result.ExitCode, "symlink followed by parent traversal exit code");
            Contains("already exists", result.StandardError, "symlink followed by parent traversal diagnostic");
            Equal(0, result.Requests.Count, "symlink followed by parent traversal request count");
            BytesEqual(original, File.ReadAllBytes(existingTarget), "symlink traversal existing output preservation");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var missingDirectory = Path.Combine(temporary.Path, "created-before-parent-traversal");
            var output = Path.Combine(missingDirectory, "..", "restored.json");
            var fixture = ReadCanonicalFixture();
            var result = await InvokeAsync(
                temporary.Path,
                output,
                SingleCandidateResponses(MakeZip(new("timings.json", fixture))));

            Equal(0, result.ExitCode, "missing directory followed by parent traversal exit code");
            BytesEqual(fixture, File.ReadAllBytes(Path.Combine(temporary.Path, "restored.json")),
                "missing directory followed by parent traversal output");
            Check(Directory.Exists(missingDirectory), "The path component before '..' was not created.");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var missingDirectory = Path.Combine(temporary.Path, "must-wait-for-valid-payload");
            var output = Path.Combine(missingDirectory, "..", "not-created.json");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                new Dictionary<string, byte[]> { [RunsEndpoint] = Runs() });

            Equal(0, result.ExitCode, "no-history parent traversal exit code");
            Contains("no successful completed build runs", result.StandardError, "no-history parent traversal warning");
            Check(!Directory.Exists(missingDirectory), "The output path parent was created before a valid payload existed.");
            Check(!File.Exists(Path.Combine(temporary.Path, "not-created.json")), "No-history parent traversal created an output file.");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var dotParent = Path.Combine(temporary.Path, "dot-parent");
            var output = Path.Combine(temporary.Path, "dot-parent", ".", "normalized.json");
            var fixture = ReadCanonicalFixture();
            var result = await InvokeAsync(
                temporary.Path,
                output,
                SingleCandidateResponses(MakeZip(new("timings.json", fixture))));

            Equal(0, result.ExitCode, "dot component output exit code");
            BytesEqual(fixture, File.ReadAllBytes(Path.Combine(dotParent, "normalized.json")), "dot component output");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var trimmedOutput = Path.Combine(temporary.Path, "trailing-separator-output.json");
            var output = trimmedOutput + Path.DirectorySeparatorChar;
            var fixture = ReadCanonicalFixture();
            var result = await InvokeAsync(
                temporary.Path,
                output,
                SingleCandidateResponses(MakeZip(new("timings.json", fixture))));

            Equal(0, result.ExitCode, "trailing separator output exit code");
            BytesEqual(fixture, File.ReadAllBytes(trimmedOutput), "trailing separator output path normalization");
        }
    }

    private static async Task BadInvocationAsync()
    {
        var invalidRepositories = new[] { "", "owner", "owner/name/extra", "../repo", "./repo", "é/repo", "owner/name?" };
        foreach (var repository in invalidRepositories)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, "invalid-repository", "timings.json");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                new Dictionary<string, byte[]>(),
                environment: DefaultEnvironment(repository, Branch));

            Equal(2, result.ExitCode, $"repository '{repository}' exit code");
            Contains("GITHUB_REPOSITORY", result.StandardError, $"repository '{repository}' diagnostic");
            Equal(0, result.Requests.Count, $"repository '{repository}' request count");
            Check(!PathExists(output), $"Invalid repository '{repository}' created output.");
        }

        var invalidBranches = new[] { "", new string('b', 256), "bad\nbranch", "bad\u007fbranch" };
        foreach (var branch in invalidBranches)
        {
            using var temporary = NewTemporaryDirectory();
            var output = Path.Combine(temporary.Path, "invalid-branch", "timings.json");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                new Dictionary<string, byte[]>(),
                environment: DefaultEnvironment(Repository, branch));

            Equal(2, result.ExitCode, "invalid branch exit code");
            Contains("GITHUB_DEFAULT_BRANCH", result.StandardError, "invalid branch diagnostic");
            Equal(0, result.Requests.Count, "invalid branch request count");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var relative = await InvokeArgumentsAsync(
                temporary.Path,
                ["restore", "--output", "relative.json"],
                new Dictionary<string, byte[]>(),
                environment: new Dictionary<string, string?>());
            Equal(2, relative.ExitCode, "relative destination exit code");
            Contains("absolute", relative.StandardError, "relative destination diagnostic");
            Equal(0, relative.Requests.Count, "relative destination request count");

            var missingRepository = await InvokeAsync(
                temporary.Path,
                Path.Combine(temporary.Path, "missing-repository.json"),
                new Dictionary<string, byte[]>(),
                environment: new Dictionary<string, string?> { ["GITHUB_DEFAULT_BRANCH"] = Branch });
            Equal(2, missingRepository.ExitCode, "missing repository exit code");
            Contains("GITHUB_REPOSITORY", missingRepository.StandardError, "missing repository diagnostic");
            Equal(0, missingRepository.Requests.Count, "missing repository request count");

            var missingBranch = await InvokeAsync(
                temporary.Path,
                Path.Combine(temporary.Path, "missing-branch.json"),
                new Dictionary<string, byte[]>(),
                environment: new Dictionary<string, string?> { ["GITHUB_REPOSITORY"] = Repository });
            Equal(2, missingBranch.ExitCode, "missing branch exit code");
            Contains("GITHUB_DEFAULT_BRANCH", missingBranch.StandardError, "missing branch diagnostic");
            Equal(0, missingBranch.Requests.Count, "missing branch request count");

            var help = await InvokeArgumentsAsync(temporary.Path, ["--help"], new Dictionary<string, byte[]>());
            Equal(0, help.ExitCode, "top-level help exit code");
            Contains("restore", help.StandardOutput, "top-level help usage");
            Equal(0, help.Requests.Count, "top-level help request count");

            var restoreHelp = await InvokeArgumentsAsync(temporary.Path, ["restore", "--help"], new Dictionary<string, byte[]>());
            Equal(0, restoreHelp.ExitCode, "restore help exit code");
            Contains("--output", restoreHelp.StandardOutput, "restore help option");
            Equal(0, restoreHelp.Requests.Count, "restore help request count");

            var invalidCommand = await InvokeArgumentsAsync(temporary.Path, ["unknown"], new Dictionary<string, byte[]>());
            Equal(2, invalidCommand.ExitCode, "invalid command exit code");
            Equal(0, invalidCommand.Requests.Count, "invalid command request count");
        }
    }

    private static async Task InputBoundariesAsync()
    {
        using var temporary = NewTemporaryDirectory();
        var fixture = ReadCanonicalFixture();
        var exactJsonLimit = PadWithWhitespace(fixture, MaxJsonBytes);
        var maximumPath = new string('p', 4089) + ".csproj";
        var supplementaryPath = string.Concat(Enumerable.Repeat("😀", 4089)) + ".csproj";
        var projects = Enumerable.Range(0, MaxProjects)
            .Select(index => new Dictionary<string, object?>
            {
                ["project"] = index switch
                {
                    0 => maximumPath,
                    1 => supplementaryPath,
                    2 => "src/Δοκιμή/🚀.csproj",
                    3 => "src/Straße.csproj",
                    4 => "src/Strasse.csproj",
                    _ => $"src/Boundary{index}/Boundary{index}.csproj"
                },
                ["seconds"] = index switch
                {
                    0 => 0,
                    1 => MaxSeconds,
                    _ => index
                }
            })
            .ToArray();
        var boundaryTimings = JsonSerializer.SerializeToUtf8Bytes(new { projects });
        var archiveAtLimit = MakeZip(
            new("timings.json", exactJsonLimit),
            new ZipEntry("boundary-projects.json", boundaryTimings));
        var archiveAtZipLimit = PadZipCommentToLength(
            MakeZip(
                new("timings.json", fixture),
                new ZipEntry("padding.bin", new byte[MaxJsonBytes - 256], CompressionLevel.NoCompression)),
            MaxZipBytes);
        var output = Path.Combine(temporary.Path, "boundary", "timings.json");
        var result = await InvokeAsync(
            temporary.Path,
            output,
            new Dictionary<string, byte[]>
            {
                [RunsEndpoint] = Runs(RunRecord(30)),
                [ArtifactsEndpoint(30)] = Artifacts(Artifact(130, size: 0)),
                [DownloadEndpoint(130)] = MakeZip(new("timings.json", boundaryTimings))
            });

        Equal(0, result.ExitCode, "maximum accepted inputs exit code");
        Equal(string.Empty, result.StandardError, "maximum accepted inputs stderr");
        BytesEqual(boundaryTimings, File.ReadAllBytes(output), "1000-row timing bytes");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(30), DownloadEndpoint(130)],
            Endpoints(result),
            "1000-row artifact request trace");

        using var zipBoundaryDirectory = NewTemporaryDirectory();
        var zipBoundaryOutput = Path.Combine(zipBoundaryDirectory.Path, "zip-boundary.json");
        var zipBoundaryResult = await InvokeAsync(
            zipBoundaryDirectory.Path,
            zipBoundaryOutput,
            SingleCandidateResponses(archiveAtZipLimit));
        Equal(0, zipBoundaryResult.ExitCode, "exact ZIP size boundary exit code");
        BytesEqual(fixture, File.ReadAllBytes(zipBoundaryOutput), "exact ZIP size boundary output");
        Equal(MaxZipBytes, archiveAtZipLimit.Length, "ZIP boundary fixture size");

        var unrelatedMemberAtLimit = MakeZip(
            new("timings.json", fixture),
            new ZipEntry("padding.bin", new byte[MaxJsonBytes], CompressionLevel.NoCompression));
        Check(unrelatedMemberAtLimit.Length <= MaxZipBytes, "The exact-size unrelated ZIP member exceeds the archive size limit.");
        using var unrelatedMemberDirectory = NewTemporaryDirectory();
        var unrelatedMemberOutput = Path.Combine(unrelatedMemberDirectory.Path, "unrelated-member.json");
        var unrelatedMemberResult = await InvokeAsync(
            unrelatedMemberDirectory.Path,
            unrelatedMemberOutput,
            SingleCandidateResponses(unrelatedMemberAtLimit));
        Equal(0, unrelatedMemberResult.ExitCode, "exact unrelated ZIP member boundary exit code");
        BytesEqual(fixture, File.ReadAllBytes(unrelatedMemberOutput), "exact unrelated ZIP member output");

        using var oversizedDownloadDirectory = NewTemporaryDirectory();
        var oversizedDownloadOutput = Path.Combine(oversizedDownloadDirectory.Path, "oversized-download", "timings.json");
        var oversizedDownloadResult = await InvokeAsync(
            oversizedDownloadDirectory.Path,
            oversizedDownloadOutput,
            new Dictionary<string, byte[]>(),
            responder: request => request.Endpoint switch
            {
                var endpoint when endpoint == RunsEndpoint => Task.FromResult(Runs(RunRecord(30))),
                var endpoint when endpoint == ArtifactsEndpoint(30) => Task.FromResult(Artifacts(Artifact(130, size: MaxZipBytes))),
                var endpoint when endpoint == DownloadEndpoint(130) => Task.FromResult(new byte[MaxZipBytes + 1]),
                _ => throw new InvalidOperationException($"Unexpected request {request.Endpoint}.")
            });
        Equal(0, oversizedDownloadResult.ExitCode, "oversized download advisory exit code");
        Contains("GitHub artifact download is unavailable", oversizedDownloadResult.StandardError, "oversized download warning");
        SequenceEqual(
            [RunsEndpoint, ArtifactsEndpoint(30), DownloadEndpoint(130)],
            Endpoints(oversizedDownloadResult),
            "oversized download request trace");
        Check(!PathExists(oversizedDownloadOutput), "An over-limit download created output.");
        Check(!Directory.Exists(Path.GetDirectoryName(oversizedDownloadOutput)!), "An over-limit download created its parent.");

        using var exactEntryDirectory = NewTemporaryDirectory();
        var exactEntryOutput = Path.Combine(exactEntryDirectory.Path, "exact-entry.json");
        var exactEntryResult = await InvokeAsync(
            exactEntryDirectory.Path,
            exactEntryOutput,
            SingleCandidateResponses(archiveAtLimit));
        Equal(0, exactEntryResult.ExitCode, "exact JSON member boundary exit code");
        BytesEqual(exactJsonLimit, File.ReadAllBytes(exactEntryOutput), "exact JSON member boundary bytes");

        var zeroSeconds = Encoding.UTF8.GetBytes(
            "{\"projects\":[{\"project\":\"a.csproj\",\"seconds\":-0}],\"extra\":\"\\ud800\"}");
        using var jsonEdgeDirectory = NewTemporaryDirectory();
        var jsonEdgeOutput = Path.Combine(jsonEdgeDirectory.Path, "json-edge.json");
        var jsonEdgeResult = await InvokeAsync(
            jsonEdgeDirectory.Path,
            jsonEdgeOutput,
            SingleCandidateResponses(MakeZip(new("timings.json", zeroSeconds))));
        Equal(0, jsonEdgeResult.ExitCode, "negative zero and escaped surrogate edge exit code");
        BytesEqual(zeroSeconds, File.ReadAllBytes(jsonEdgeOutput), "negative zero and escaped surrogate bytes");

        // The baseline accepts escaped unpaired UTF-16 code units and preserves their original JSON bytes.
        var escaped = """
            {"projects":[{"project":"src/\ud800.csproj","seconds":1}], "\ud800":true, "\ud801":false}
            """u8.ToArray();
        var escapedOutput = Path.Combine(jsonEdgeDirectory.Path, "escaped.json");
        var escapedResult = await InvokeAsync(jsonEdgeDirectory.Path, escapedOutput,
            SingleCandidateResponses(MakeZip(new("timings.json", escaped))));
        Equal(0, escapedResult.ExitCode, "escaped project and property names");
        BytesEqual(escaped, File.ReadAllBytes(escapedOutput), "original bytes with escaped project and property names");
    }

    private static async Task CodecAcceptanceAsync()
    {
        var expectedPayload = Convert.FromBase64String(CodecPayloadBase64);
        Equal(67, expectedPayload.Length, "codec expected payload length");
        Equal(CodecPayloadSha256, Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant(), "codec expected payload SHA-256");
        using (var document = JsonDocument.Parse(expectedPayload))
        {
            var project = document.RootElement.GetProperty("projects")[0];
            Equal("src/App/App.csproj", project.GetProperty("project").GetString(), "codec payload project");
            Equal(84, project.GetProperty("seconds").GetInt32(), "codec payload seconds");
        }

        var positiveFixtures = new[]
        {
            ("bzip2", Bzip2ArchiveSha256),
            ("lzma", LzmaArchiveSha256)
        };
        foreach (var (name, expectedArchiveHash) in positiveFixtures)
        {
            using var temporary = NewTemporaryDirectory();
            var archive = ReadCodecFixture(name);
            Equal(expectedArchiveHash, Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), name + " immutable archive SHA-256");
            var output = Path.Combine(temporary.Path, name, "timings.json");
            var result = await InvokeAsync(temporary.Path, output, SingleCandidateResponses(archive));

            Equal(0, result.ExitCode, name + " exit code");
            Equal(string.Empty, result.StandardError, name + " stderr");
            SequenceEqual([RunsEndpoint, ArtifactsEndpoint(8), DownloadEndpoint(18)], Endpoints(result), name + " request trace");
            var actualPayload = File.ReadAllBytes(output);
            BytesEqual(expectedPayload, actualPayload, name + " original payload bytes");
            Equal(CodecPayloadSha256, Convert.ToHexString(SHA256.HashData(actualPayload)).ToLowerInvariant(), name + " payload SHA-256");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var archive = ReadCodecFixture("method-9-deflate64-stored-block");
            Equal(Method9ArchiveSha256, Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), "method 9 immutable archive SHA-256");
            var output = Path.Combine(temporary.Path, "method-9", "timings.json");
            var result = await InvokeAsync(temporary.Path, output, SingleCandidateResponses(archive));

            Equal(0, result.ExitCode, "method 9 advisory exit code");
            Contains("no valid coverage schedule timings artifact", result.StandardError, "method 9 rejection warning");
            SequenceEqual([RunsEndpoint, ArtifactsEndpoint(8), DownloadEndpoint(18)], Endpoints(result), "method 9 request trace");
            Check(!PathExists(output), "The decodable method 9 fixture was accepted.");
            Check(!Directory.Exists(Path.GetDirectoryName(output)!), "Rejected method 9 created its output parent.");
        }
    }

    private static async Task OutputOwnershipAsync()
    {
        var fixture = ReadCanonicalFixture();
        var validArchive = MakeZip(new("timings.json", fixture));

        using (var temporary = NewTemporaryDirectory())
        {
            var output = Path.Combine(temporary.Path, "race", "timings.json");
            var parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            var competingBytes = Encoding.UTF8.GetBytes("another process owns this file");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                SingleCandidateResponses(validArchive),
                responder: request =>
                {
                    if (request.Endpoint == DownloadEndpoint(18))
                    {
                        File.WriteAllBytes(output, competingBytes);
                        return Task.FromResult(validArchive);
                    }
                    return Task.FromResult(SingleCandidateResponse(request.Endpoint, validArchive));
                });

            Equal(2, result.ExitCode, "competing file exit code");
            Contains("could not create --output", result.StandardError, "competing file diagnostic");
            BytesEqual(competingBytes, File.ReadAllBytes(output), "competing file preservation");
            Equal(3, result.Requests.Count, "requests before create race");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var output = Path.Combine(temporary.Path, "partial", "timings.json");
            var unrelated = Path.Combine(temporary.Path, "partial", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(unrelated, "preserve sibling");
            var result = await InvokeAsync(
                temporary.Path,
                output,
                SingleCandidateResponses(validArchive),
                writeBytes: (stream, bytes) =>
                {
                    stream.Write(bytes, 0, Math.Min(7, bytes.Length));
                    throw new IOException("simulated failure after a partial write");
                });

            Equal(2, result.ExitCode, "partial write exit code");
            Contains("could not create --output", result.StandardError, "partial write diagnostic");
            Check(!PathExists(output), "The helper left its partial output file behind.");
            Check(Directory.Exists(Path.GetDirectoryName(output)!), "A newly created output parent was unexpectedly removed.");
            Equal("preserve sibling", File.ReadAllText(unrelated), "unrelated sibling preservation");
        }

        using (var temporary = NewTemporaryDirectory())
        {
            var parentFile = Path.Combine(temporary.Path, "parent-file");
            File.WriteAllText(parentFile, "preserve parent file");
            var output = Path.Combine(parentFile, "timings.json");
            var result = await InvokeAsync(temporary.Path, output, SingleCandidateResponses(validArchive));

            Equal(2, result.ExitCode, "non-directory parent exit code");
            Contains("could not create --output", result.StandardError, "non-directory parent diagnostic");
            Equal("preserve parent file", File.ReadAllText(parentFile), "non-directory parent preservation");
            Equal(3, result.Requests.Count, "non-directory parent request count");
        }
    }

    private static async Task<RunResult> InvokeAsync(
        string temporaryRoot,
        string output,
        IReadOnlyDictionary<string, byte[]> responses,
        IReadOnlyDictionary<string, string?>? environment = null,
        ManualTimeProvider? clock = null,
        Func<RequestTrace, Task<byte[]>>? responder = null,
        Action<Stream, byte[]>? writeBytes = null)
    {
        return await InvokeArgumentsAsync(
            temporaryRoot,
            ["restore", "--output", output],
            responses,
            environment,
            clock,
            responder,
            writeBytes);
    }

    private static async Task<RunResult> InvokeArgumentsAsync(
        string temporaryRoot,
        string[] arguments,
        IReadOnlyDictionary<string, byte[]> responses,
        IReadOnlyDictionary<string, string?>? environment = null,
        ManualTimeProvider? clock = null,
        Func<RequestTrace, Task<byte[]>>? responder = null,
        Action<Stream, byte[]>? writeBytes = null)
    {
        var env = environment ?? DefaultEnvironment();
        var requests = new List<RequestTrace>();
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var result = await CoverageScheduleHistory.RunAsync(
            arguments,
            name => env.TryGetValue(name, out var value) ? value : null,
            stdout,
            stderr,
            async (endpoint, maxBytes, timeout) =>
            {
                var request = new RequestTrace(endpoint, maxBytes, timeout);
                requests.Add(request);
                if (responder is not null)
                {
                    return await responder(request);
                }
                if (!responses.TryGetValue(endpoint, out var response))
                {
                    throw new InvalidOperationException($"Unexpected request endpoint '{endpoint}'.");
                }
                return response;
            },
            clock ?? TimeProvider.System,
            writeBytes);

        return new(result, stdout.ToString(), stderr.ToString(), requests);
    }

    private static IReadOnlyDictionary<string, byte[]> SingleCandidateResponses(byte[] archive)
    {
        return new Dictionary<string, byte[]>
        {
            [RunsEndpoint] = Runs(RunRecord(8)),
            [ArtifactsEndpoint(8)] = Artifacts(Artifact(18)),
            [DownloadEndpoint(18)] = archive
        };
    }

    private static byte[] SingleCandidateResponse(string endpoint, byte[] archive) => endpoint switch
    {
        var value when value == RunsEndpoint => Runs(RunRecord(8)),
        var value when value == ArtifactsEndpoint(8) => Artifacts(Artifact(18)),
        var value when value == DownloadEndpoint(18) => archive,
        _ => throw new InvalidOperationException($"Unexpected request endpoint '{endpoint}'.")
    };

    private static Dictionary<string, string?> DefaultEnvironment(string repository = Repository, string branch = Branch)
    {
        return new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = repository,
            ["GITHUB_DEFAULT_BRANCH"] = branch
        };
    }

    private static byte[] Runs(params Dictionary<string, object?>[] records)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["workflow_runs"] = records });
    }

    private static Dictionary<string, object?> RunRecord(
        object? id,
        string branch = Branch,
        string status = "completed",
        string conclusion = "success",
        string createdAt = "2026-11-09T12:00:00Z",
        string? eventName = "push",
        object? headRepository = null,
        bool includeEvent = true,
        bool includeHeadRepository = true,
        bool nullHeadRepository = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["head_branch"] = branch,
            ["status"] = status,
            ["conclusion"] = conclusion,
            ["created_at"] = createdAt
        };
        if (includeEvent)
        {
            result["event"] = eventName;
        }
        if (includeHeadRepository)
        {
            result["head_repository"] = nullHeadRepository
                ? null
                : headRepository is string name
                    ? new Dictionary<string, object?> { ["full_name"] = name }
                    : headRepository ?? new Dictionary<string, object?> { ["full_name"] = Repository };
        }
        return result;
    }

    private static byte[] Artifacts(params Dictionary<string, object?>[] artifacts)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["artifacts"] = artifacts });
    }

    private static Dictionary<string, object?> Artifact(
        object? id,
        string name = ArtifactName,
        bool expired = false,
        object? size = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name,
            ["expired"] = expired,
            ["size_in_bytes"] = size ?? 1024
        };
    }

    private static byte[] TimingBytes(string project, object? seconds)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            projects = new[] { new { project, seconds } }
        });
    }

    private static byte[] TimingBytes(params (string Project, long Seconds)[] projects)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            projects = projects.Select(project => new { project = project.Project, seconds = project.Seconds })
        });
    }

    private static byte[] MakeZip(ZipEntry firstEntry, params ZipEntry[] remainingEntries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in remainingEntries.Prepend(firstEntry))
            {
                var zippedEntry = archive.CreateEntry(entry.Name, entry.Compression);
                using var output = zippedEntry.Open();
                output.Write(entry.Bytes);
            }
        }
        return buffer.ToArray();
    }

    private static byte[] SetZipCompressionMethod(byte[] archive, ushort method)
    {
        var result = (byte[])archive.Clone();
        var localHeader = FindSignature(result, [0x50, 0x4b, 0x03, 0x04]);
        var centralHeader = FindSignature(result, [0x50, 0x4b, 0x01, 0x02]);
        WriteUInt16(result, localHeader + 8, method);
        WriteUInt16(result, centralHeader + 10, method);
        return result;
    }

    private static byte[] SetZipEncryptedFlag(byte[] archive)
    {
        var result = (byte[])archive.Clone();
        var localHeader = FindSignature(result, [0x50, 0x4b, 0x03, 0x04]);
        var centralHeader = FindSignature(result, [0x50, 0x4b, 0x01, 0x02]);
        WriteUInt16(result, localHeader + 6, (ushort)(ReadUInt16(result, localHeader + 6) | 1));
        WriteUInt16(result, centralHeader + 8, (ushort)(ReadUInt16(result, centralHeader + 8) | 1));
        return result;
    }

    private static byte[] CorruptDeflate(byte[] archive)
    {
        var result = (byte[])archive.Clone();
        var localHeader = FindSignature(result, [0x50, 0x4b, 0x03, 0x04]);
        var nameLength = ReadUInt16(result, localHeader + 26);
        var extraLength = ReadUInt16(result, localHeader + 28);
        var compressedStart = localHeader + 30 + nameLength + extraLength;
        result[compressedStart] = 0xff;
        return result;
    }

    private static byte[] CorruptStoredCrc(byte[] archive)
    {
        var result = (byte[])archive.Clone();
        var localHeader = FindSignature(result, [0x50, 0x4b, 0x03, 0x04]);
        var centralHeader = FindSignature(result, [0x50, 0x4b, 0x01, 0x02]);
        WriteUInt32(result, localHeader + 14, ReadUInt32(result, localHeader + 14) ^ 1u);
        WriteUInt32(result, centralHeader + 16, ReadUInt32(result, centralHeader + 16) ^ 1u);
        return result;
    }

    private static byte[] PadZipCommentToLength(byte[] archive, int targetLength)
    {
        var endRecord = FindSignatureFromEnd(archive, [0x50, 0x4b, 0x05, 0x06]);
        var commentLength = targetLength - archive.Length;
        Check(commentLength >= 0 && commentLength <= ushort.MaxValue, "The ZIP comment cannot reach the requested boundary.");
        var result = new byte[targetLength];
        archive.CopyTo(result, 0);
        WriteUInt16(result, endRecord + 20, (ushort)commentLength);
        return result;
    }

    private static byte[] PadWithWhitespace(byte[] bytes, int targetLength)
    {
        Check(bytes.Length <= targetLength, "The canonical timing fixture exceeds the JSON boundary.");
        var result = new byte[targetLength];
        bytes.CopyTo(result, 0);
        Array.Fill(result, (byte)' ', bytes.Length, targetLength - bytes.Length);
        return result;
    }

    private static int FindSignature(byte[] bytes, byte[] signature)
    {
        for (var offset = 0; offset <= bytes.Length - signature.Length; offset++)
        {
            if (bytes.AsSpan(offset, signature.Length).SequenceEqual(signature))
            {
                return offset;
            }
        }
        throw new InvalidDataException("The ZIP fixture is missing an expected header.");
    }

    private static int FindSignatureFromEnd(byte[] bytes, byte[] signature)
    {
        for (var offset = bytes.Length - signature.Length; offset >= 0; offset--)
        {
            if (bytes.AsSpan(offset, signature.Length).SequenceEqual(signature))
            {
                return offset;
            }
        }
        throw new InvalidDataException("The ZIP fixture is missing its end record.");
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static IReadOnlyList<string> Endpoints(RunResult result) =>
        result.Requests.Select(request => request.Endpoint).ToArray();

    private static string RunsEndpointFor(string repository, string branch)
    {
        var encodedBranch = Uri.EscapeDataString(branch)
            .Replace("%20", "+", StringComparison.Ordinal);
        return $"repos/{repository}/actions/workflows/build.yml/runs?branch={encodedBranch}&event=push&status=success&per_page=10";
    }

    private static string ArtifactsEndpoint(int runId) =>
        $"repos/{Repository}/actions/runs/{runId}/artifacts?per_page=100";

    private static string DownloadEndpoint(int artifactId) =>
        $"repos/{Repository}/actions/artifacts/{artifactId}/zip";

    private static byte[] ReadCodecFixture(string name)
    {
        var path = Path.Combine(Root, "tests", "coverage-schedule-history", "fixtures", name + ".zip.b64");
        return Convert.FromBase64String(File.ReadAllText(path));
    }

    private static byte[] ReadCanonicalFixture() =>
        File.ReadAllBytes(Path.Combine(Root, "tests", "coverage-schedule-history", "fixtures", "timings.json"));

    private static void AssertCanonicalFixtureShape(byte[] fixture)
    {
        using var document = JsonDocument.Parse(fixture);
        var root = document.RootElement;
        Equal("coverage-schedule-history", root.GetProperty("source").GetString(), "canonical fixture source");

        var projects = root.GetProperty("projects").EnumerateArray().ToArray();
        SequenceEqual(
            [
                "tests/First.Tests/First.Tests.csproj",
                "tests/Slow.Tests/Slow.Tests.csproj",
                "tests/After.Tests/After.Tests.csproj"
            ],
            projects.Select(project => project.GetProperty("project").GetString()),
            "canonical fixture project order");
        SequenceEqual(
            [5, 50, 90],
            projects.Select(project => project.GetProperty("seconds").GetInt32()),
            "canonical fixture durations");

        var hangDiagnostics = projects[0].GetProperty("hangDiagnostics");
        Equal(999, hangDiagnostics.GetProperty("schemaVersion").GetInt32(), "canonical fixture extra schema version");
        Equal(
            "ignored",
            hangDiagnostics.GetProperty("futureStatus").GetProperty("value").GetString(),
            "canonical fixture extra future status");
        Check(Encoding.UTF8.GetString(fixture).Contains("\"source\" :", StringComparison.Ordinal),
            "The canonical fixture must preserve its noncanonical spacing.");
        Check(fixture.Contains((byte)'\n'), "The canonical fixture must preserve its line breaks.");
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        var current = new DirectoryInfo(sourceDirectory ?? Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ".github", "scripts", "CoverageScheduleHistory.cs")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the checkout containing CoverageScheduleHistory.cs.");
    }

    private static TemporaryDirectory NewTemporaryDirectory() => new();

    private static bool PathExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidDataException($"{label}: expected '{expected}', received '{actual}'.");
        }
    }

    private static void Contains(string expected, string actual, string label)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{label}: expected to find '{expected}' in '{actual}'.");
        }
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string label)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidDataException($"{label}: expected {expected.Length} bytes, received {actual.Length} different bytes.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidDataException(
                $"{label}: expected [{string.Join(", ", expectedArray)}], received [{string.Join(", ", actualArray)}].");
        }
    }
}
