namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    private static async Task<CoverageFanInEngineResult> CollectAsync(CoverageFanInEngineRequest r,
        CoverageTextWriters writers, InvocationState state, CancellationToken token)
    {
        var producer = Producer(r);
        if (producer is not null && producer.Value.GetProperty("platform").GetString() != Platform())
            throw new InvalidDataException("Native producer platform differs from current host.");
        var collection = Full(r.CollectionDirectory!);
        RejectOverlap([collection], r.OutputDirectory);
        if (Directory.Exists(collection) || File.Exists(collection)) throw new IOException("Collection destination must be fresh.");
        var locator = new StrictReportGeneratorRuntimeLocator();
        _ = locator.ResolveReportGeneratorDll();
        var runner = new CliWrapCoverageRunProcessRunner();
        var workflow = new CoverageRunWorkflow(runner,
            new CoverageFanInNormalization(new CoverageRunReportGenerator(runner, locator), r.Expected.Bounds), TimeProvider.System);
        var projects = producer is { } native && native.TryGetProperty("testProjects", out var inventory)
            && inventory.ValueKind == System.Text.Json.JsonValueKind.Array
            ? inventory.EnumerateArray().Select(x => x.GetString()!).ToArray() : r.Expected.TestProjects;
        var filter = producer?.GetProperty("filter").GetString();
        var request = new CoverageRunRequest(r.SolutionPath, projects, [], collection, r.Expected.Configuration,
            r.Parallelism, CoverageRunScheduleMode.InputOrder, null, [], r.NoRestore, !r.NoBuild, r.NoBuild,
            r.Expected.Instrumentation.GetProperty("include").GetString(), r.Expected.Instrumentation.GetProperty("exclude").GetString()!,
            false, false, [], ["console;verbosity=normal"], filter is null ? [] : ["--filter", filter],
            CoverageRunTestResultFormat.Junit, false, true, "normal", TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5), CoverageRunWatchdogMode.Fail, CoverageRunDriver.Collector, false,
            new(TimeProvider.System, TimeProvider.System.GetTimestamp(), TimeSpan.FromSeconds(r.DeadlineSeconds)))
        { ArtifactRetention = "full", WorkingDirectory = Full(r.RepositoryRoot) };
        CoverageRunResult result;
        await writers.Output.WriteLineAsync("Coverage collection started for the declared inventory.").ConfigureAwait(false);
        // Child logs can contain arbitrary test output and host paths. Keep them in the
        // caller-selected collection directory; only bounded stage progress crosses this facade.
        try
        {
            result = await workflow.RunAsync(request,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), token).ConfigureAwait(false);
        }
        finally { state.EffectsStarted |= Directory.Exists(collection); }
        if (!result.Success) throw new InvalidDataException("Required collection did not complete.");
        await writers.Output.WriteLineAsync("Coverage collection completed; validating retained producer evidence.").ConfigureAwait(false);
        await VerifySourceAsync(r, token).ConfigureAwait(false);
        string? raw = null;
        string? timingsHash = null;
        IReadOnlyDictionary<string, string>? reportHashes = null;
        IReadOnlyDictionary<string, string>? rawSet = null;
        if (producer is not null)
        {
            var reports = FindNativeReports(collection, projects, r.Expected, token, hash => timingsHash = hash,
                hashes => reportHashes = hashes);
            if (projects.Length == 1) raw = reports[projects[0]];
            else rawSet = reports;
        }
        var captureRequest = r with
        {
            CoveragePath = rawSet is null ? result.CoveragePath : null,
            TimingsPath = producer is null || rawSet is not null ? Path.Join(collection, "timings.json") : null,
            RawCoveragePath = raw,
            RawCoveragePaths = rawSet
        };
        using var io = new CoverageFanInIO(r.Expected.Bounds, token);
        io.Acquire(InputDirectories("capture", captureRequest).Select(x => (x, false)), requireCoordination: false);
        if (timingsHash is not null && CoverageFanInIO.Hash(io.Read(Path.Join(collection, "timings.json"))) != timingsHash)
            throw new InvalidDataException("Collected project-to-attachment timing binding changed before capture.");
        if (reportHashes is not null) VerifySelectedNativeReports(io, reportHashes);
        state.Observation = "CollectedExecution";
        var files = Capture(captureRequest, io, state);
        io.VerifyBindings();
        io.AdmitOutput(files.Values);
        await PublishAsync(r, "capture", files, state, token, io).ConfigureAwait(false);
        return state.Result("Succeeded");
    }

    /// <summary>Binds selection-time original report bytes to the retained capture IO through publication.</summary>
    internal static void VerifySelectedNativeReports(CoverageFanInIO io, IReadOnlyDictionary<string, string> hashes)
    {
        foreach (var (path, hash) in hashes)
            if (CoverageFanInIO.Hash(io.Read(path, xml: true)) != hash)
                throw new InvalidDataException("Selected collector attachment changed before capture.");
    }
}
