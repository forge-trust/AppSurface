using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Orders three fixed phases and refuses credentialed closeout after any custody or settlement failure.</summary>
/// <remarks>The coordinator is a private CI comparison mechanism. It never alters product admission or coverage policy.</remarks>
[SupportedOSPlatform("linux")]
internal static class OwnerCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Finite synthetic-control checkpoints; these never contain paths, credentials or child text.</summary>
    internal enum ControlCheckpoint { NotStarted, BeforePhaseCustody, Phase, Result, EscapeMarker, HostCanary, AfterPhaseCustody, MutationPhase, MutationCustody, Complete }

    /// <summary>The last entered synthetic-control operation; a retained setup marker may describe an earlier phase.</summary>
    internal static ControlCheckpoint ControlStep { get; private set; }

    internal static async Task<int> ControlsAsync(string repository, string sourceCapture, string evidenceRoot, CancellationToken token)
    {
        ControlStep = ControlCheckpoint.NotStarted;
        OwnerLayout? layout = null;
        var results = new List<object>();
        var passed = false;
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var previous = Environment.GetEnvironmentVariable("GH_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", "synthetic-isolation-" + canary);
        try
        {
            layout = await OwnerWorkspace.CreateAsync(repository, sourceCapture, null, null, evidenceRoot, token);
            await File.WriteAllTextAsync(layout.HostRoot + "/credential-canary", canary, token);
            File.SetUnixFileMode(layout.HostRoot + "/credential-canary", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await WriteOwnedAsync(layout, OwnerPaths.Stage + "/archive-canary", "archive\n", token);
            await WriteOwnedAsync(layout, OwnerPaths.Preparation, "comparison-only\n", token);
            await OwnerCustody.EnrollPreparedAsync(layout, token);
            foreach (var control in ControlProbe.Names.Where(name => name != "mutate-source"))
            {
                ControlStep = ControlCheckpoint.BeforePhaseCustody;
                await OwnerWorkspace.VerifyCustodyAsync(layout, token);
                var budget = TimeSpan.FromSeconds(control == "quota" ? 180 : control is "ignore-stop" or "output-overflow" or "spawn-escape" ? 20 : 90);
                ControlStep = ControlCheckpoint.Phase;
                var phase = await PhaseRunner.RunAsync(layout, OwnerPhase.Control,
                    new("control", null, control, null), budget, token);
                ControlStep = ControlCheckpoint.Result;
                var expected = phase.Settled && (control switch
                {
                    "ignore-stop" => phase.TimedOut,
                    "spawn-escape" => phase.TimedOut && phase.EscapeObserved,
                    "output-overflow" => phase.OutputOverflow,
                    _ => phase.ExitCode == 0 && !phase.TimedOut && !phase.OutputOverflow,
                });
                results.Add(new { control, expected, result = phase });
                if (!expected) throw new IsolationException("control-failed");
                if (control == "spawn-escape")
                {
                    ControlStep = ControlCheckpoint.EscapeMarker;
                    var marker = await File.ReadAllTextAsync(layout.Host(OwnerPaths.Output + "/escaped-child"), token);
                    if (!uint.TryParse(marker, out var innerPid) || innerPid <= 1)
                        throw new IsolationException("escape-child-not-observed");
                    File.Delete(layout.Host(OwnerPaths.Output + "/escaped-child"));
                }
                ControlStep = ControlCheckpoint.HostCanary;
                if (await File.ReadAllTextAsync(layout.HostRoot + "/credential-canary", token) != canary)
                    throw new IsolationException("host-canary-changed");
                ControlStep = ControlCheckpoint.AfterPhaseCustody;
                await OwnerWorkspace.VerifyCustodyAsync(layout, token);
            }
            // Writable target trees remain subject to the original post-replay source check.
            ControlStep = ControlCheckpoint.MutationPhase;
            var mutation = await PhaseRunner.RunAsync(layout, OwnerPhase.Control,
                new("control", null, "mutate-source", null), TimeSpan.FromSeconds(90), token);
            if (!mutation.Settled || mutation.ExitCode != 0) throw new IsolationException("mutation-probe");
            var rejected = false;
            ControlStep = ControlCheckpoint.MutationCustody;
            try { await OwnerWorkspace.VerifyCustodyAsync(layout, token); }
            catch (Exception error) when (error is IsolationException or InvalidDataException) { rejected = true; }
            results.Add(new { control = "mutate-source", expected = rejected, result = mutation });
            if (!rejected) throw new IsolationException("custody-drift-not-rejected");
            ControlStep = ControlCheckpoint.Complete;
            passed = true;
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", previous);
            if (layout is not null)
            {
                // Never serialize canary/token material, exception text, or target-authored status as authority.
                await PublishReportAsync(layout.EvidenceRoot, "isolation-controls.json", new
                {
                    schema = "issue779-completed-isolation-controls-v1",
                    passed,
                    controls = results,
                    uid = layout.Uid,
                    gid = layout.Gid,
                    admission_created = false,
                    qualification_complete = false,
                    native_acceptance = false,
                    gate_eligible = false,
                    coverage_credit = false,
                });
                // Intentional source-tamper control is quarantined: the workspace is preserved for diagnosis.
                await OwnerWorkspace.DisposeAsync(layout, false, CancellationToken.None);
            }
        }
    }

    internal static async Task<int> CompletedAsync(string repository, string validatorCapture, string sourceCapture,
        string eventPath, string evidenceRoot, CancellationToken token)
    {
        var context = ReadProtectedContext();
        var secret = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) > 8192
            || secret.Any(char.IsControl)) throw new IsolationException("credential-input");
        OwnerLayout? layout = null;
        var results = new List<PhaseResult>();
        var settled = true;
        var completed = false;
        var transitions = new OwnerTransition();
        try
        {
            layout = await OwnerWorkspace.CreateAsync(repository, sourceCapture, validatorCapture, eventPath, evidenceRoot, token);
            await OwnerWorkspace.VerifyCustodyAsync(layout, token);
            settled = false;
            var prepare = await PhaseRunner.RunAsync(layout, OwnerPhase.Prepare,
                new("prepare", secret, null, context), TimeSpan.FromSeconds(1320), token);
            results.Add(prepare);
            settled = prepare.Settled;
            RequireSuccess(prepare);
            transitions.Prepared(prepare);
            await OwnerCustody.EnrollPreparedAsync(layout, token);
            await OwnerWorkspace.VerifyCustodyAsync(layout, token);
            settled = false;
            var replay = await PhaseRunner.RunAsync(layout, OwnerPhase.Replay,
                new("replay", null, null, null), TimeSpan.FromSeconds(1200), token);
            results.Add(replay);
            settled = replay.Settled;
            RequireSuccess(replay);
            transitions.Replayed(replay);
            await OwnerWorkspace.EnrollReplayAsync(layout, token);
            // This independent root-side check must finish before any token-bearing child is started.
            await OwnerWorkspace.VerifyCustodyAsync(layout, token);
            transitions.CustodyVerified();
            transitions.RequireCloseAllowed();
            settled = false;
            var close = await PhaseRunner.RunAsync(layout, OwnerPhase.Close,
                new("close", secret, null, context), TimeSpan.FromSeconds(180), token);
            results.Add(close);
            settled = close.Settled;
            RequireSuccess(close);
            transitions.Closed(close);
            await OwnerWorkspace.VerifyCustodyAsync(layout, token);
            var report = layout.Host(OwnerPaths.Report + "/observation.json");
            var body = await File.ReadAllBytesAsync(report, token);
            if (body.Length is 0 or > 65536) throw new IsolationException("report-bound");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("schema").GetString() != "issue779-completed-validation-observation-v1")
                throw new IsolationException("report-schema");
            await PublishBytesAsync(layout.EvidenceRoot, "comparison-observation.json", body);
            completed = true;
            return 0;
        }
        finally
        {
            secret = null;
            if (layout is not null)
            {
                await PublishReportAsync(layout.EvidenceRoot, "outer-owner.json", new
                {
                    schema = "issue779-completed-outer-owner-v1",
                    comparison_completed = completed,
                    phases = results,
                    admission_created = false,
                    native_acceptance = false,
                    qualification_complete = false,
                    gate_eligible = false,
                    coverage_credit = false,
                });
                await OwnerWorkspace.DisposeAsync(layout, settled && completed, CancellationToken.None);
            }
        }
    }

    internal static Dictionary<string, string> ReadProtectedContext() =>
        OwnerCompletionPolicy.Validate(OwnerCompletionPolicy.ContextNames.ToDictionary(name => name,
            name => Environment.GetEnvironmentVariable(name) ?? throw new IsolationException("protected-context")));

    private static void RequireSuccess(PhaseResult result)
    {
        if (result.ExitCode != 0 || !result.Settled || result.TimedOut || result.OutputOverflow)
            throw new IsolationException("phase-failed");
    }

    private static async Task WriteOwnedAsync(OwnerLayout layout, string path, string body, CancellationToken token)
    {
        var host = layout.Host(path);
        await using (var file = new FileStream(host, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await file.WriteAsync(Encoding.UTF8.GetBytes(body), token);
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (Chown(host, layout.Uid, layout.Gid) != 0) throw new IsolationException("control-owner");
    }

    private static Task PublishReportAsync(string directory, string name, object report) =>
        PublishBytesAsync(directory, name, JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));

    private static async Task PublishBytesAsync(string directory, string name, byte[] bytes)
    {
        await using var file = new FileStream(Path.Join(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        File.SetUnixFileMode(Path.Join(directory, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint uid, uint gid);
}
