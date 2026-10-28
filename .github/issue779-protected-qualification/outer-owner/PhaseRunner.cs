using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Root-owned private transient-unit runner for one bounded child phase.</summary>
[SupportedOSPlatform("linux")]
internal static class PhaseRunner
{
    private const int MaximumStreamBytes = 16 * 1024 * 1024;
    private const int MaximumReadyLineBytes = 256;

    internal static async Task<PhaseResult> RunAsync(OwnerLayout layout, OwnerPhase phase,
        PhaseRelease release, TimeSpan budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ValidateRequest(layout, phase, release, budget);

        var unit = "appsurface-completed-" + PhaseName(phase) + "-" + Guid.NewGuid().ToString("N") + ".service";
        var standardOutputPath = Path.Combine(layout.EvidenceRoot, unit + ".stdout.log");
        var standardErrorPath = Path.Combine(layout.EvidenceRoot, unit + ".stderr.log");
        var unitName = unit[..^".service".Length];
        var timer = Stopwatch.StartNew();
        var cleanupReserve = phase == OwnerPhase.Control && budget <= TimeSpan.FromSeconds(30)
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromMilliseconds(Math.Min(30_000, Math.Max(1_000, budget.TotalMilliseconds / 10)));
        var workBudget = budget - cleanupReserve;
        if (workBudget <= TimeSpan.Zero) throw new IsolationException("phase-budget");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget);
        using var workDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        workDeadline.CancelAfter(workBudget);

        Process? systemdRun = null;
        Task<PumpResult>? stdoutPump = null;
        Task<PumpResult>? stderrPump = null;
        var outputOverflow = 0;
        var timedOut = false;
        var exitCode = 2;
        var settled = false;
        var systemdRunJoined = false;
        var pumpsJoined = false;
        Exception? primaryError = null;
        NativeFacts.PhaseWitness? witness = null;
        var spawnEscapeObserved = false;
        var phaseStage = PhaseStage.SystemdStart;
        void SignalOverflow()
        {
            Interlocked.Exchange(ref outputOverflow, 1);
            workDeadline.Cancel();
        }

        try
        {
            phaseStage = PhaseStage.SdkValidation;
            await NativeFacts.VerifySdkAsync(layout, workDeadline.Token).ConfigureAwait(false);
            phaseStage = PhaseStage.SystemdStart;
            Directory.CreateDirectory(layout.EvidenceRoot);
            var stdoutLog = new FileStream(standardOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stderrLog = new FileStream(standardErrorPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using (stdoutLog)
            using (stderrLog)
            {
                var start = BuildSystemdRun(layout, phase, unitName, budget - TimeSpan.FromSeconds(1));
                systemdRun = Process.Start(start) ?? throw new IsolationException("phase-start");
                phaseStage = PhaseStage.Ready;
                var ready = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                stdoutPump = PumpAsync(systemdRun.StandardOutput.BaseStream, ready, isStdout: true,
                    SignalOverflow, workDeadline.Token);
                stderrPump = PumpAsync(systemdRun.StandardError.BaseStream, null, isStdout: false,
                    SignalOverflow, workDeadline.Token);

                var readyLine = await ready.Task.WaitAsync(workDeadline.Token).ConfigureAwait(false);
                var namespacePid = ParseReadyLine(readyLine);
                phaseStage = PhaseStage.NativeValidation;
                witness = await NativeFacts.VerifyReadyProcessAsync(layout, unit, namespacePid, phase, workDeadline.Token).ConfigureAwait(false);

                var frame = SerializeRelease(phase, release);
                phaseStage = PhaseStage.Release;
                try
                {
                    await systemdRun.StandardInput.BaseStream.WriteAsync(frame, workDeadline.Token).ConfigureAwait(false);
                    await systemdRun.StandardInput.BaseStream.FlushAsync(workDeadline.Token).ConfigureAwait(false);
                    systemdRun.StandardInput.Close();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(frame);
                }

                phaseStage = PhaseStage.Child;
                var output = Task.WhenAll(stdoutPump, stderrPump);
                await Task.WhenAll(systemdRun.WaitForExitAsync(workDeadline.Token), output).ConfigureAwait(false);
                systemdRunJoined = true;
                pumpsJoined = true;
                exitCode = systemdRun.ExitCode;
                if (Volatile.Read(ref outputOverflow) != 0) throw new IsolationException("phase-output-overflow");
                var streams = await output.ConfigureAwait(false);
                var tokenBytes = release.Token is null ? null : Encoding.UTF8.GetBytes(release.Token);
                try
                {
                    await WriteSanitizedAsync(stdoutLog, streams[0].Bytes, tokenBytes, workDeadline.Token).ConfigureAwait(false);
                    await WriteSanitizedAsync(stderrLog, streams[1].Bytes, tokenBytes, workDeadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(streams[0].Bytes);
                    CryptographicOperations.ZeroMemory(streams[1].Bytes);
                    if (tokenBytes is not null) CryptographicOperations.ZeroMemory(tokenBytes);
                }
                phaseStage = PhaseStage.Stop;
                await StopAndKillAsync(unitName, workDeadline.Token).ConfigureAwait(false);
                phaseStage = PhaseStage.Settlement;
                settled = await RequireSettlementAsync(witness, workDeadline.Token).ConfigureAwait(false);
                if (!settled) throw new IsolationException("phase-unsettled");
            }
        }
        catch (OperationCanceledException error) when (workDeadline.IsCancellationRequested || deadline.IsCancellationRequested)
        {
            timedOut = Volatile.Read(ref outputOverflow) == 0 && !token.IsCancellationRequested && timer.Elapsed >= workBudget;
            primaryError = error;
            if (token.IsCancellationRequested) throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            primaryError = error;
        }
        finally
        {
            if (systemdRun is not null)
            {
                if (!settled)
                {
                    var remaining = budget - timer.Elapsed;
                    using var cleanupDeadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                    if (phase == OwnerPhase.Control && release.Control == "spawn-escape" && timedOut && witness is not null)
                    {
                        try { spawnEscapeObserved = NativeFacts.VerifySpawnEscapeMarker(layout, witness); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or IsolationException)
                        { primaryError = new IsolationException("control-spawn-missing"); }
                        if (!spawnEscapeObserved) primaryError = new IsolationException("control-spawn-missing");
                    }
                    phaseStage = PhaseStage.Stop;
                    await StopAndKillAsync(unitName, cleanupDeadline.Token).ConfigureAwait(false);
                    if (!systemdRun.HasExited && !cleanupDeadline.IsCancellationRequested)
                    {
                        try { await systemdRun.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false); }
                        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
                    }
                    if (!systemdRun.HasExited)
                    {
                        try { systemdRun.Kill(entireProcessTree: true); }
                        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                        if (!cleanupDeadline.IsCancellationRequested)
                        {
                            try { await systemdRun.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false); }
                            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { }
                        }
                    }
                    systemdRunJoined = systemdRun.HasExited;
                    phaseStage = PhaseStage.PipeJoin;
                    if (stdoutPump is not null && stderrPump is not null && !cleanupDeadline.IsCancellationRequested)
                    {
                        try { await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false); }
                        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or IsolationException) { }
                    }
                    pumpsJoined = stdoutPump?.IsCompleted == true && stderrPump?.IsCompleted == true;
                    phaseStage = PhaseStage.Settlement;
                    var cgroupEmpty = witness is not null && await WaitForEmptyAsync(witness, cleanupDeadline.Token).ConfigureAwait(false);
                    settled = systemdRunJoined && pumpsJoined && cgroupEmpty;
                }
                systemdRun.Dispose();
            }
            witness?.Dispose();
        }

        timer.Stop();
        if (Volatile.Read(ref outputOverflow) != 0) exitCode = 2;
        if (!settled && primaryError is null) primaryError = new IsolationException("phase-unsettled");
        if (primaryError is not null && !timedOut) exitCode = 2;
        return new(exitCode, settled, timedOut, Volatile.Read(ref outputOverflow) != 0, unit,
            Math.Max(0, (long)timer.Elapsed.TotalMilliseconds), standardOutputPath, standardErrorPath,
            GetFailureCategory(primaryError, timedOut, Volatile.Read(ref outputOverflow) != 0, exitCode, settled,
                phaseStage, spawnEscapeObserved), spawnEscapeObserved);
    }

    private static ProcessStartInfo BuildSystemdRun(OwnerLayout layout, OwnerPhase phase, string unitName, TimeSpan runtime)
    {
        var start = new ProcessStartInfo("/usr/bin/systemd-run")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LANG"] = "C";
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add("--wait");
        start.ArgumentList.Add("--unit=" + unitName);
        start.ArgumentList.Add("--service-type=exec");
        AddProperty(start, "RootDirectory", layout.RootFileSystem);
        AddProperty(start, "WorkingDirectory", "/");
        AddProperty(start, "User", "root");
        AddProperty(start, "Group", "root");
        AddProperty(start, "Type", "exec");
        AddProperty(start, "KillMode", "control-group");
        AddProperty(start, "Delegate", "no");
        AddProperty(start, "ProtectSystem", "strict");
        AddProperty(start, "ReadWritePaths", string.Join(' ', NamespacePathsFor(phase, writable: true)));
        AddProperty(start, "ReadOnlyPaths", string.Join(' ', NamespacePathsFor(phase, writable: false)));
        var sdkPath = NativeFacts.ReadCanonicalSdkPath(layout);
        if (!Directory.Exists(sdkPath)) throw new IsolationException("sdk-host-path");
        AddProperty(start, "BindReadOnlyPaths", sdkPath + ":/usr/share/dotnet");
        AddProperty(start, "NoNewPrivileges", "yes");
        AddProperty(start, "RestrictSUIDSGID", "yes");
        AddProperty(start, "PrivateDevices", "yes");
        AddProperty(start, "ProtectKernelTunables", "yes");
        AddProperty(start, "ProtectKernelModules", "yes");
        AddProperty(start, "ProtectKernelLogs", "yes");
        AddProperty(start, "ProtectControlGroups", "yes");
        AddProperty(start, "ProtectClock", "yes");
        AddProperty(start, "ProtectHostname", "yes");
        AddProperty(start, "LockPersonality", "yes");
        AddProperty(start, "MemoryMax", "6G");
        AddProperty(start, "CPUQuota", "200%");
        AddProperty(start, "TasksMax", "128");
        AddProperty(start, "LimitCORE", "0");
        AddProperty(start, "UMask", "0077");
        AddProperty(start, "TimeoutStopSec", "1s");
        AddProperty(start, "RuntimeMaxSec", runtime.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
        AddProperty(start, "CapabilityBoundingSet", "CAP_SYS_ADMIN CAP_SETUID CAP_SETGID CAP_SETPCAP CAP_SYS_CHROOT");

        start.ArgumentList.Add("--");
        start.ArgumentList.Add("/usr/bin/unshare");
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add("--fork");
        start.ArgumentList.Add("--mount-proc");
        start.ArgumentList.Add("--kill-child=KILL");
        start.ArgumentList.Add("/usr/bin/setpriv");
        start.ArgumentList.Add("--reuid=" + layout.Uid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--regid=" + layout.Gid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--groups=" + layout.Gid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--bounding-set=-all");
        start.ArgumentList.Add("--inh-caps=-all");
        start.ArgumentList.Add("--ambient-caps=-all");
        start.ArgumentList.Add("--no-new-privs");
        start.ArgumentList.Add("/usr/bin/env");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("PATH=/usr/bin:/bin");
        start.ArgumentList.Add("HOME=/nonexistent");
        start.ArgumentList.Add("LANG=C.UTF-8");
        start.ArgumentList.Add("LC_ALL=C");
        start.ArgumentList.Add(OwnerPaths.Dotnet);
        start.ArgumentList.Add(OwnerPaths.Assembly);
        start.ArgumentList.Add("phase");
        start.ArgumentList.Add(PhaseName(phase));
        return start;
    }

    private static async Task<PumpResult> PumpAsync(Stream input, TaskCompletionSource<byte[]>? ready,
        bool isStdout, Action overflow, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var content = new MemoryStream();
        using var readyLine = new MemoryStream();
        long total = 0;
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > MaximumStreamBytes)
                {
                    overflow();
                    ready?.TrySetException(new IsolationException("phase-output-overflow"));
                    throw new IsolationException("phase-output-overflow");
                }
                content.Write(buffer, 0, count);
                if (ready is null || !ready.Task.IsCompleted)
                {
                    if (!isStdout) continue;
                    for (var index = 0; index < count; index++)
                    {
                        if (readyLine.Length >= MaximumReadyLineBytes)
                        {
                            ready?.TrySetException(new IsolationException("ready-line-size"));
                            throw new IsolationException("ready-line-size");
                        }
                        readyLine.WriteByte(buffer[index]);
                        if (buffer[index] == (byte)'\n')
                        {
                            ready?.TrySetResult(readyLine.ToArray());
                            break;
                        }
                    }
                }
            }
            if (isStdout && ready is not null && !ready.Task.IsCompleted)
                ready.TrySetException(new IsolationException("ready-line-missing"));
            return new(total, content.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(content.GetBuffer());
            CryptographicOperations.ZeroMemory(readyLine.GetBuffer());
        }
    }

    private static async Task<bool> RequireSettlementAsync(NativeFacts.PhaseWitness witness, CancellationToken token)
    {
        var first = NativeFacts.IsCgroupEmpty(witness);
        if (!first) return false;
        await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        return NativeFacts.IsCgroupEmpty(witness);
    }

    private static async Task<bool> WaitForEmptyAsync(NativeFacts.PhaseWitness witness, CancellationToken token)
    {
        var consecutive = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (NativeFacts.IsCgroupEmpty(witness))
                {
                    if (++consecutive >= 2) return true;
                }
                else consecutive = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or IsolationException or UnauthorizedAccessException) { consecutive = 0; }
        }
        return false;
    }

    private static async Task StopAndKillAsync(string unitName, CancellationToken cancellationToken)
    {
        using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopDeadline.CancelAfter(TimeSpan.FromSeconds(1));
        try { await NativeFacts.RunTextAsync("/usr/bin/systemctl", ["stop", unitName], stopDeadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or IsolationException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
        try { await NativeFacts.RunTextAsync("/usr/bin/systemctl", ["kill", "--kill-whom=all", "--signal=KILL", unitName], cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or IsolationException or InvalidOperationException or OperationCanceledException or TimeoutException) { }
    }

    private static byte[] SerializeRelease(OwnerPhase phase, PhaseRelease release)
    {
        if (release.Phase != PhaseName(phase)) throw new IsolationException("release-phase");
        var json = JsonSerializer.SerializeToUtf8Bytes(release, new JsonSerializerOptions
        { MaxDepth = 4, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (json.Length >= 32 * 1024) throw new IsolationException("release-size");
        var frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        CryptographicOperations.ZeroMemory(json);
        return frame;
    }

    private static uint ParseReadyLine(byte[] line)
    {
        var text = Encoding.ASCII.GetString(line);
        const string prefix = "ISOLATION_READY:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('\n')
            || text[..^1].Contains('\r')
            || !uint.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var pid) || pid != 1) throw new IsolationException("ready-line");
        return pid;
    }

    private static void ValidateRequest(OwnerLayout layout, OwnerPhase phase, PhaseRelease release, TimeSpan budget)
    {
        if (!OperatingSystem.IsLinux() || !Enum.IsDefined(phase) || !Path.IsPathFullyQualified(layout.RootFileSystem)
            || !Path.IsPathFullyQualified(layout.EvidenceRoot) || budget <= TimeSpan.Zero
            || budget > MaximumBudget(phase) || release.Phase != PhaseName(phase)) throw new IsolationException("phase-request");
        if (phase is OwnerPhase.Prepare or OwnerPhase.Close)
        {
            if (release.Token is null || release.Token.Length == 0 || Encoding.UTF8.GetByteCount(release.Token) > 8192
                || release.Token.Any(static character => character is < '!' or > '~')
                || release.Control is not null || release.Context is null || !HasExactContext(release.Context))
                throw new IsolationException("phase-credential-release");
        }
        else if (phase == OwnerPhase.Replay)
        {
            if (release.Token is not null || release.Context is not null || release.Control is not null)
                throw new IsolationException("phase-replay-release");
        }
        else if (release.Token is not null || release.Context is not null || release.Control is null
            || !ControlProbe.Names.Contains(release.Control, StringComparer.Ordinal))
            throw new IsolationException("phase-control-release");
    }

    private static bool HasExactContext(IReadOnlyDictionary<string, string> context)
    {
        string[] keys = ["GITHUB_ACTIONS", "GITHUB_EVENT_NAME", "GITHUB_REPOSITORY", "GITHUB_REF",
            "GITHUB_REF_PROTECTED", "GITHUB_WORKFLOW_REF", "GITHUB_REPOSITORY_ID", "GITHUB_SHA", "GITHUB_WORKFLOW_SHA"];
        return context.Count == keys.Length && keys.All(context.ContainsKey)
            && context.Values.All(static value => value.Length <= 1024 && !value.Any(char.IsControl));
    }

    private static TimeSpan MaximumBudget(OwnerPhase phase) => phase switch
    {
        OwnerPhase.Prepare => TimeSpan.FromSeconds(1320),
        OwnerPhase.Replay => TimeSpan.FromSeconds(1200),
        OwnerPhase.Close => TimeSpan.FromSeconds(180),
        OwnerPhase.Control => TimeSpan.FromSeconds(1200),
        _ => TimeSpan.Zero
    };
    private static void AddProperty(ProcessStartInfo start, string key, string value) => start.ArgumentList.Add("--property=" + key + "=" + value);
    private static IEnumerable<string> NamespacePathsFor(OwnerPhase phase, bool writable)
    {
        var write = new HashSet<string>(StringComparer.Ordinal);
        if (phase is OwnerPhase.Prepare or OwnerPhase.Replay or OwnerPhase.Control)
        {
            write.Add(OwnerPaths.Source); write.Add(OwnerPaths.Native); write.Add(OwnerPaths.Output); write.Add(OwnerPaths.Handoff);
        }
        write.Add("/tmp");
        if (phase == OwnerPhase.Prepare) write.Add(OwnerPaths.Stage);
        if (phase == OwnerPhase.Close) write.Add(OwnerPaths.Report);
        var all = new[] { OwnerPaths.Validator, OwnerPaths.Input, OwnerPaths.Source, OwnerPaths.Native,
            OwnerPaths.Source + "/.git", OwnerPaths.Native + "/.git", OwnerPaths.Stage, OwnerPaths.Handoff,
            OwnerPaths.Output, OwnerPaths.Report, "/tmp" };
        var paths = all.Where(path => write.Contains(path) == writable).ToList();
        if (!writable && phase is OwnerPhase.Replay or OwnerPhase.Control or OwnerPhase.Close)
            paths.Add(OwnerPaths.Preparation);
        return paths;
    }
    private static string PhaseName(OwnerPhase phase) => phase switch
    {
        OwnerPhase.Prepare => "prepare", OwnerPhase.Replay => "replay", OwnerPhase.Close => "close", OwnerPhase.Control => "control",
        _ => throw new IsolationException("phase")
    };
    private sealed record PumpResult(long Count, byte[] Bytes);

    private static async Task WriteSanitizedAsync(Stream destination, byte[] bytes, byte[]? secret, CancellationToken token)
    {
        try
        {
            if (secret is null || secret.Length == 0)
            {
                await destination.WriteAsync(bytes, token).ConfigureAwait(false);
                await destination.FlushAsync(token).ConfigureAwait(false);
                return;
            }
            ReadOnlySpan<byte> marker = "[REDACTED]"u8;
            using var safe = new MemoryStream(bytes.Length);
            for (var index = 0; index < bytes.Length;)
            {
                if (index + secret.Length <= bytes.Length && bytes.AsSpan(index, secret.Length).SequenceEqual(secret))
                {
                    safe.Write(marker);
                    index += secret.Length;
                }
                else safe.WriteByte(bytes[index++]);
            }
            await destination.WriteAsync(safe.GetBuffer().AsMemory(0, checked((int)safe.Length)), token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }


    private static string? GetFailureCategory(Exception? error, bool timedOut, bool overflow, int exitCode, bool settled,
        PhaseStage stage, bool spawnEscapeObserved)
    {
        if (overflow) return "phase-output-overflow";
        if (error is IsolationException { Message: "control-spawn-missing" }) return "control-spawn-missing";
        if (timedOut) return stage switch
        {
            PhaseStage.SdkValidation => "phase-timeout-sdk-validation",
            PhaseStage.SystemdStart => "phase-timeout-start",
            PhaseStage.Ready => "phase-timeout-ready",
            PhaseStage.NativeValidation => "phase-timeout-native-validation",
            PhaseStage.Release => "phase-timeout-release",
            PhaseStage.Child => spawnEscapeObserved ? "control-spawn-settled" : "phase-timeout-child",
            PhaseStage.Stop => "phase-timeout-stop",
            PhaseStage.PipeJoin => "phase-timeout-pipe-join",
            PhaseStage.Settlement => "phase-timeout-settlement",
            _ => "phase-timeout-start"
        };
        if (error is IsolationException isolation)
        {
            return isolation.Message switch
            {
                "phase-start" or "phase-request" or "release-phase" or "release-size" or "phase-budget" => isolation.Message,
                "phase-credential-release" or "phase-replay-release" or "phase-control-release" => "phase-release-rejected",
                "phase-unsettled" or "unit-cgroup" or "unit-process-count" or "unit-launcher-ambiguous"
                    or "unit-native-validation" or "sdk-host-path" or "sdk-host-directory" or "sdk-version" => "phase-native-validation",
                "phase-output-overflow" => "phase-output-overflow",
                "ready-line-missing" or "ready-line-size" or "ready-line" or "ready-pid" => "phase-ready-protocol",
                "phase-native-validation" => "phase-native-validation",
                _ => "phase-runtime"
            };
        }
        if (!settled) return "phase-unsettled";
        if (exitCode == 0 && error is null) return null;
        return stage switch
        {
            PhaseStage.SystemdStart => "phase-start-failure",
            PhaseStage.Ready => "phase-ready-failure",
            PhaseStage.NativeValidation => "phase-native-validation",
            PhaseStage.Release => "phase-release-failure",
            PhaseStage.Child => "phase-child-exit",
            PhaseStage.Stop => "phase-stop-failure",
            PhaseStage.PipeJoin => "phase-pipe-join-failure",
            PhaseStage.Settlement => "phase-unsettled",
            PhaseStage.SdkValidation => "phase-sdk-validation",
            _ => "phase-runtime"
        };
    }

    private enum PhaseStage { SdkValidation, SystemdStart, Ready, NativeValidation, Release, Child, Stop, PipeJoin, Settlement }
}
