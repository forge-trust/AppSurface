using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Creates and owns one quota-bounded Linux workspace for the fixed completed-run phases.</summary>
/// <remarks>This is private infrastructure. Its filesystem facts do not grant workflow, native, or product authority.</remarks>
internal static class OwnerWorkspace
{
    private const long ImageBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumProcessOutput = 16 * 1024 * 1024;
    private const int MaximumCaptureBytes = 1024 * 1024;
    private const string HostRootPrefix = "/var/lib/appsurface-completed-";
    private const string UidMarkerPrefix = "/run/appsurface-completed-uid-";
    private sealed record Reservation(uint Id, string MarkerPath, SafeFileHandle Marker);
    private static readonly ConcurrentDictionary<string, SafeFileHandle> Reservations = new(StringComparer.Ordinal);

    /// <summary>Creates a fresh private ext4 workspace and binds its source trees to the supplied captures.</summary>
    /// <param name="repository">A clean full local repository clone containing both captured commits.</param>
    /// <param name="sourceCapture">The bounded capture for the completed source revision.</param>
    /// <param name="validatorCapture">The bounded current validator capture, or null for source-only controls.</param>
    /// <param name="eventPath">A bounded event JSON file for completed execution, or null for synthetic controls.</param>
    /// <param name="evidenceRoot">A fresh absolute directory that receives owner logs and reports.</param>
    /// <param name="token">Cancellation for setup; a partially-created workspace is quarantined on failure.</param>
    internal static async Task<OwnerLayout> CreateAsync(string repository, string sourceCapture,
        string? validatorCapture, string? eventPath, string evidenceRoot, CancellationToken token)
    {
        RequireSupportedHost();
        var repo = CanonicalExistingDirectory(repository, "repository");
        var evidenceParent = CanonicalExistingDirectory(Path.GetDirectoryName(evidenceRoot) ?? "", "evidence-parent");
        Require(Path.IsPathFullyQualified(evidenceRoot) && Path.GetFullPath(evidenceRoot) == evidenceRoot
            && StringComparer.Ordinal.Equals(Path.GetDirectoryName(evidenceRoot), evidenceParent), "evidence-path");
        var sourceBytes = await OwnerCustody.ReadBoundedInputAsync(sourceCapture, MaximumCaptureBytes, token)
            .ConfigureAwait(false);
        var source = OwnerCustody.ParseCapture(sourceBytes);
        var validatorBytes = validatorCapture is null
            ? sourceBytes.ToArray()
            : await OwnerCustody.ReadBoundedInputAsync(validatorCapture, MaximumCaptureBytes, token).ConfigureAwait(false);
        var validator = OwnerCustody.ParseCapture(validatorBytes);
        var eventBytes = eventPath is null
            ? Encoding.UTF8.GetBytes("{\"schema\":\"issue779-outer-control-event-v1\"}\n")
            : await OwnerCustody.ReadBoundedInputAsync(eventPath, 128 * 1024, token).ConfigureAwait(false);
        ValidateEvent(eventBytes);

        var sdk = await ResolveSdkAsync(token).ConfigureAwait(false);
        await RequireLocalCommitAsync(repo, validator.Source, token).ConfigureAwait(false);
        await RequireLocalCommitAsync(repo, source.Source, token).ConfigureAwait(false);
        var ancestry = await RunFixedAsync("/usr/bin/git",
            ["--no-optional-locks", "-c", "core.fsmonitor=false", "-C", repo, "merge-base", "--is-ancestor", source.Source, validator.Source],
            4096, 4096, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        Require(ancestry.ExitCode == 0, "source-not-validator-ancestor");
        var evidence = CreateEvidenceRoot(evidenceRoot);

        Reservation? reservation = null;
        string? hostRoot = null;
        var mounted = false;
        try
        {
            reservation = ReserveIdentity(token);
            Require(Reservations.TryAdd(reservation.MarkerPath, reservation.Marker), "uid-marker-state");
            hostRoot = HostRootPrefix + Guid.NewGuid().ToString("N");
            CreateRoot(hostRoot);
            WriteRootMarker(hostRoot, reservation);
            var image = Path.Combine(hostRoot, "rootfs.img");
            var mountpoint = Path.Combine(hostRoot, "fs");
            Directory.CreateDirectory(mountpoint);
            SetMode(mountpoint, 0x1c0); // root-only mountpoint.
            await RunRequiredAsync("/usr/bin/truncate", ["-s", ImageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), image],
                token, "image-create").ConfigureAwait(false);
            await RunRequiredAsync("/usr/sbin/mkfs.ext4", ["-F", "-q", "-m", "0", "-N", "131072", image], token, "image-format")
                .ConfigureAwait(false);
            var loopResult = await RunFixedAsync("/usr/sbin/losetup", ["--find", "--show", image], 4096, 4096,
                TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            Require(loopResult.ExitCode == 0, "loop-attach");
            var loopDevice = StrictSingleLine(loopResult.StandardOutput);
            Require(loopDevice.StartsWith("/dev/loop", StringComparison.Ordinal)
                && loopDevice.Skip("/dev/loop".Length).All(char.IsAsciiDigit), "loop-device");
            await File.WriteAllTextAsync(Path.Combine(hostRoot, "loop.device"), loopDevice + "\n", Encoding.ASCII, token)
                .ConfigureAwait(false);
            SetOwner(Path.Combine(hostRoot, "loop.device"), 0, 0);
            SetMode(Path.Combine(hostRoot, "loop.device"), 0x180);
            await RunRequiredAsync("/usr/bin/mount", ["-t", "ext4", "-o", "nosuid,nodev", loopDevice, mountpoint], token, "image-mount")
                .ConfigureAwait(false);
            mounted = true;
            var layout = new OwnerLayout(hostRoot, mountpoint, reservation.Id, reservation.Id,
                OwnerPaths.Assembly, evidence);
            CreateRootFilesystem(layout);
            await InstallRootFilesystemAsync(layout, sdk, token).ConfigureAwait(false);

            await CloneAtCaptureAsync(repo, layout.Host(OwnerPaths.Validator), validator, token).ConfigureAwait(false);
            await CloneAtCaptureAsync(repo, layout.Host(OwnerPaths.Source), source, token).ConfigureAwait(false);
            await CloneAtCaptureAsync(repo, layout.Host(OwnerPaths.Native), source, token).ConfigureAwait(false);
            await CopyOwnerAssemblyAsync(layout, token).ConfigureAwait(false);
            await WriteInputAsync(layout.Host(OwnerPaths.SourceCapture), sourceBytes, reservation.Id, token).ConfigureAwait(false);
            await WriteInputAsync(layout.Host(OwnerPaths.ValidatorCapture), validatorBytes, reservation.Id, token).ConfigureAwait(false);
            await WriteInputAsync(layout.Host(OwnerPaths.Event), eventBytes, reservation.Id, token).ConfigureAwait(false);
            await WriteInputAsync(layout.Host(OwnerPaths.Input + "/immutable-canary"), Encoding.UTF8.GetBytes("immutable\n"), reservation.Id, token)
                .ConfigureAwait(false);
            await WriteInputAsync(layout.Host(OwnerPaths.Input + "/host-canary-path"),
                Encoding.UTF8.GetBytes(Path.Combine(hostRoot, "credential-canary") + "\n"), reservation.Id, token).ConfigureAwait(false);
            await SetCheckoutOwnerAsync(layout.Host(OwnerPaths.Validator), reservation.Id, reservation.Id, token).ConfigureAwait(false);
            await SetCheckoutOwnerAsync(layout.Host(OwnerPaths.Source), reservation.Id, reservation.Id, token).ConfigureAwait(false);
            await SetCheckoutOwnerAsync(layout.Host(OwnerPaths.Native), reservation.Id, reservation.Id, token).ConfigureAwait(false);
            foreach (var path in new[] { OwnerPaths.Stage, OwnerPaths.Input, OwnerPaths.Handoff, OwnerPaths.Output, OwnerPaths.Report })
                SetTreeOwner(layout.Host(path), reservation.Id, reservation.Id);
            await OwnerCustody.InitializeAsync(layout, sourceBytes, validatorBytes, eventBytes, token).ConfigureAwait(false);
            OwnerCustody.VerifyExt4Image(layout.RootFileSystem);
            OwnerCustody.VerifyRootHost(layout.HostRoot);
            return layout;
        }
        catch
        {
            // Incomplete roots are intentionally retained; cleanup must not infer ownership from a partial setup.
            if (mounted && hostRoot is not null)
            {
                var mountPath = Path.Combine(hostRoot, "fs");
                if (TryUnmount(mountPath)) TryDetachLoop(Path.Combine(hostRoot, "loop.device"));
            }
            if (hostRoot is not null) TryQuarantine(hostRoot);
            else if (reservation is not null)
            {
                if (Reservations.TryRemove(reservation.MarkerPath, out var marker)) marker.Dispose();
                try { File.Delete(reservation.MarkerPath); } catch { }
            }
            throw;
        }
    }

    /// <summary>Rechecks pinned roots, source trees, fixed input bytes, and enrolled archives between phases.</summary>
    internal static Task VerifyCustodyAsync(OwnerLayout layout, CancellationToken token) =>
        OwnerCustody.VerifyCustodyAsync(layout, token);

    /// <summary>Enrolls the successful prepare outputs after the owner has joined the phase unit.</summary>
    internal static Task EnrollPreparedAsync(OwnerLayout layout, CancellationToken token) =>
        OwnerCustody.EnrollPreparedAsync(layout, token);

    /// <summary>Enrolls the replay result after the replay unit has settled.</summary>
    internal static Task EnrollReplayAsync(OwnerLayout layout, CancellationToken token) =>
        OwnerCustody.EnrollReplayAsync(layout, token);

    /// <summary>Unmounts and removes only a settled, still-owned workspace; ambiguous roots are preserved.</summary>
    internal static async Task DisposeAsync(OwnerLayout layout, bool settled, CancellationToken token)
    {
        if (!settled)
        {
            try
            {
                OwnerCustody.VerifyRootHost(layout.HostRoot);
                TryQuarantine(layout.HostRoot);
            }
            catch { }
            return;
        }
        token.ThrowIfCancellationRequested();
        OwnerCustody.VerifyRootHost(layout.HostRoot);
        await OwnerCustody.VerifyCustodyAsync(layout, token).ConfigureAwait(false);
        var reservationPath = UidMarkerPrefix + layout.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        VerifyUidMarker(reservationPath, layout.Uid);
        await RunRequiredAsync("/usr/bin/umount", [layout.RootFileSystem], token, "image-unmount").ConfigureAwait(false);
        var loopDevice = StrictSingleLine(OwnerCustody.ReadRootFile(Path.Combine(layout.HostRoot, "loop.device"), 0, 0x180, 128));
        await RunRequiredAsync("/usr/sbin/losetup", ["--detach", loopDevice], token, "loop-detach").ConfigureAwait(false);
        OwnerCustody.ReleaseAsyncState(layout);
        RemoveOwnedTree(layout.HostRoot);
        if (Reservations.TryRemove(reservationPath, out var heldMarker)) heldMarker.Dispose();
        File.Delete(reservationPath);
    }

    /// <summary>Runs a fixed system utility with a closed environment and bounded pipes/deadline.</summary>
    internal static async Task<(int ExitCode, byte[] StandardOutput, byte[] StandardError)> RunFixedAsync(
        string executable, IReadOnlyList<string> arguments, int stdoutLimit, int stderrLimit, TimeSpan timeout,
        CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || stdoutLimit is < 0 or > MaximumProcessOutput
            || stderrLimit is < 0 or > MaximumProcessOutput || !FixedExecutable(executable))
            throw new IsolationException("utility-contract");
        using var process = new Process();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/root";
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        process.StartInfo = start;
        try
        {
            if (!process.Start()) throw new IsolationException("utility-start");
        }
        catch (Exception error) when (error is not IsolationException and not OutOfMemoryException and not StackOverflowException)
        {
            throw new IsolationException("utility-start");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, stdoutLimit, timeoutSource.Token);
        var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, stderrLimit, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return (process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            throw new IsolationException(token.IsCancellationRequested ? "utility-cancelled" : "utility-timeout");
        }
        catch (IOException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            throw new IsolationException("utility-output-limit");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var amount = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (amount == 0) return output.ToArray();
            if (output.Length + amount > limit) throw new IOException("utility-output-limit");
            output.Write(buffer, 0, amount);
        }
    }

    private static bool FixedExecutable(string executable) => executable is "/usr/bin/git" or "/usr/bin/truncate"
        or "/usr/sbin/mkfs.ext4" or "/usr/sbin/losetup" or "/usr/bin/mount" or "/usr/bin/umount"
        or "/usr/bin/getent" or "/usr/bin/dotnet" or "/usr/share/dotnet/dotnet"
        or "/usr/local/bin/dotnet" or "/usr/local/share/dotnet/dotnet"
        or "/usr/bin/ldd" or "/usr/bin/which" or "/usr/bin/cp";

    private static async Task RunRequiredAsync(string executable, IReadOnlyList<string> args, CancellationToken token,
        string category)
    {
        var result = await RunFixedAsync(executable, args, 16 * 1024, 16 * 1024, TimeSpan.FromSeconds(60), token)
            .ConfigureAwait(false);
        Require(result.ExitCode == 0, category);
    }

    private static void RequireSupportedHost()
    {
        Require(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "linux-x64-required");
        Require(OwnerCustody.EffectiveUid == 0, "root-required");
        Require(Directory.Exists("/run/systemd/system") && File.Exists("/sys/fs/cgroup/cgroup.controllers"),
            "systemd-cgroupv2-required");
        var parent = OwnerCustody.StatPath("/var/lib");
        var run = OwnerCustody.StatPath("/run");
        Require(parent.Type == OwnerCustody.DirectoryType && parent.Uid == 0 && (parent.Mode & 0x12) == 0,
            "host-parent");
        Require(run.Type == OwnerCustody.DirectoryType && run.Uid == 0, "run-parent");
    }

    private static async Task<string> ResolveSdkAsync(CancellationToken token)
    {
        var candidates = new[] { "/usr/share/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/usr/bin/dotnet" };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var result = await RunFixedAsync(path, ["--version"], 4096, 4096, TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);
            if (result.ExitCode != 0) continue;
            if (Encoding.ASCII.GetString(result.StandardOutput).Trim() == "10.0.401")
                return Path.GetDirectoryName(Path.GetFullPath(new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path))!;
        }
        throw new IsolationException("sdk-10.0.401-required");
    }

    private static async Task RequireLocalCommitAsync(string repository, string commit, CancellationToken token)
    {
        var result = await RunFixedAsync("/usr/bin/git",
            ["--no-optional-locks", "-c", "core.fsmonitor=false", "-C", repository, "cat-file", "-e", commit + "^{commit}"],
            4096, 4096, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        Require(result.ExitCode == 0, "capture-commit-unavailable");
    }

    private static async Task CloneAtCaptureAsync(string repository, string destination, OwnerCustody.Capture capture,
        CancellationToken token)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var clone = await RunFixedAsync("/usr/bin/git", ["--no-optional-locks", "-c", "core.fsmonitor=false",
            "clone", "--no-hardlinks", "--no-checkout", "--local", repository, destination],
            16 * 1024, 16 * 1024, TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
        Require(clone.ExitCode == 0, "source-clone");
        var checkout = await RunFixedAsync("/usr/bin/git", ["--no-optional-locks", "-c", "core.fsmonitor=false",
            "-c", "core.hooksPath=/dev/null", "-C", destination, "checkout", "--detach", capture.Source],
            16 * 1024, 16 * 1024, TimeSpan.FromMinutes(3), token).ConfigureAwait(false);
        Require(checkout.ExitCode == 0, "source-checkout");
        OwnerCustody.SealGitMetadata(destination);
        await OwnerCustody.VerifyCheckoutAsync(destination, capture, capture.Source, token).ConfigureAwait(false);
    }

    private static string CanonicalExistingDirectory(string value, string category)
    {
        Require(Path.IsPathFullyQualified(value) && Path.GetFullPath(value) == value && Directory.Exists(value), category);
        Require((OwnerCustody.StatPath(value).Mode & 0xf000) == OwnerCustody.DirectoryType, category);
        return value;
    }

    private static string CreateEvidenceRoot(string value)
    {
        Require(Path.IsPathFullyQualified(value) && Path.GetFullPath(value) == value
            && !Directory.Exists(value) && !File.Exists(value), "evidence-not-fresh");
        var parent = Path.GetDirectoryName(value) ?? throw new IsolationException("evidence-parent");
        var parentFact = OwnerCustody.StatPath(parent);
        Require(parentFact.Type == OwnerCustody.DirectoryType && parentFact.Uid == 0 && (parentFact.Mode & 0x12) == 0,
            "evidence-parent-metadata");
        Directory.CreateDirectory(value);
        SetOwner(value, 0, 0);
        SetMode(value, 0x1c0);
        var node = OwnerCustody.StatPath(value);
        Require(node.Type == OwnerCustody.DirectoryType && node.Uid == 0 && node.Gid == 0
            && node.Mode == 0x1c0 && node.Links >= 2, "evidence-root-metadata");
        return value;
    }

    private static void ValidateEvent(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= 128 * 1024, "event-size");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions
            { MaxDepth = 16, CommentHandling = System.Text.Json.JsonCommentHandling.Disallow });
            Require(document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object, "event-shape");
        }
        catch (System.Text.Json.JsonException) { throw new IsolationException("event-json"); }
    }

    private static Reservation ReserveIdentity(CancellationToken token)
    {
        for (uint id = 62000; id <= 64999; id++)
        {
            token.ThrowIfCancellationRequested();
            var markerPath = UidMarkerPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var created = false;
            try
            {
                var fd = OpenExclusive(markerPath, 0x40 | 0x80 | 0x2 | 0x80000 | 0x20000, 0x180);
                if (fd < 0) continue;
                created = true;
                using (var createHandle = new SafeFileHandle((IntPtr)fd, true))
                using (var stream = new FileStream(createHandle, FileAccess.Write, 1024, false))
                {
                    var body = Encoding.ASCII.GetBytes($"issue779-completed-uid-v1\n{id}\n");
                    stream.Write(body);
                    stream.Flush(true);
                }
                if (NameExists("passwd", id, token) || NameExists("group", id, token)
                    || OwnerCustody.ProcessUsesId(id, token))
                {
                    File.Delete(markerPath);
                    continue;
                }
                return new Reservation(id, markerPath, OpenExistingMarker(markerPath));
            }
            catch (IOException)
            {
                if (created) try { File.Delete(markerPath); } catch { }
            }
            catch
            {
                if (created) try { File.Delete(markerPath); } catch { }
                throw;
            }
        }
        throw new IsolationException("uid-range-exhausted");
    }

    private static bool NameExists(string database, uint id, CancellationToken token)
    {
        var path = database == "passwd" ? "/etc/passwd" : "/etc/group";
        foreach (var line in File.ReadLines(path))
        {
            token.ThrowIfCancellationRequested();
            var fields = line.Split(':');
            if (fields.Length > 2 && uint.TryParse(fields[2], out var value) && value == id) return true;
        }
        var result = RunFixedAsync("/usr/bin/getent", [database, id.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            4096, 4096, TimeSpan.FromSeconds(5), token).GetAwaiter().GetResult();
        if (result.ExitCode == 0) return true;
        if (result.ExitCode == 2) return false;
        throw new IsolationException("getent-failed");
    }

    private static void CreateRoot(string path)
    {
        Require(!Directory.Exists(path) && !File.Exists(path), "root-collision");
        Directory.CreateDirectory(path);
        SetOwner(path, 0, 0);
        SetMode(path, 0x1c0);
        var fact = OwnerCustody.StatPath(path);
        Require(fact.Type == OwnerCustody.DirectoryType && fact.Uid == 0 && fact.Gid == 0 && fact.Mode == 0x1c0,
            "root-ownership");
    }

    private static void WriteRootMarker(string root, Reservation reservation)
    {
        var path = Path.Combine(root, "owner.marker");
        var raw = Encoding.ASCII.GetBytes($"issue779-completed-owner-v1\n{reservation.Id}\n{Guid.NewGuid():N}\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(raw);
        stream.Flush(true);
        SetOwner(path, 0, 0);
        SetMode(path, 0x180);
    }

    private static void VerifyUidMarker(string path, uint id)
    {
        var info = OwnerCustody.StatPath(path);
        Require(info.Type == OwnerCustody.RegularType && info.Uid == 0 && info.Gid == 0 && info.Mode == 0x180 && info.Links == 1,
            "uid-marker-metadata");
        var bytes = OwnerCustody.ReadRootFile(path, 0, 0x180, 128);
        Require(Encoding.ASCII.GetString(bytes) == $"issue779-completed-uid-v1\n{id}\n", "uid-marker-content");
    }

    private static void CreateRootFilesystem(OwnerLayout layout)
    {
        var directories = new[]
        {
            "/bin", "/sbin", "/usr", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/lib/python3.12",
            "/usr/lib/python3.12/lib-dynload", "/lib", "/lib64", "/etc", "/etc/ssl", "/etc/ssl/certs",
            "/dev", "/proc", "/run", "/tmp", "/root", "/home", "/home/runner", "/nonexistent",
            "/opt/appsurface", "/opt/appsurface/owner", "/opt/appsurface/validator", "/opt/appsurface/native-source",
            "/opt/appsurface/stage", "/opt/appsurface/input", "/opt/appsurface/handoff", "/opt/appsurface/output",
            "/opt/appsurface/report", "/home/runner/work", "/home/runner/work/AppSurface",
            "/home/runner/work/AppSurface/AppSurface", "/usr/share", "/usr/share/dotnet",
        };
        foreach (var item in directories)
        {
            var path = layout.Host(item);
            Directory.CreateDirectory(path);
            SetOwner(path, 0, 0);
            SetMode(path, item is "/root" ? 0x1c0u : item is "/tmp" ? 0x3ffu : 0x1edu);
        }
    }

    private static async Task InstallRootFilesystemAsync(OwnerLayout layout, string sdkDirectory, CancellationToken token)
    {
        await CopyBinaryClosureAsync(layout, ["bash", "sh", "git", "python3", "python3.12", "env", "setpriv", "unshare", "id",
            "grep", "sed", "awk", "find", "rm", "mkdir", "cp", "mv", "chmod", "chown", "cat", "head", "tail",
            "touch", "stat", "sha256sum", "timeout", "uname", "readlink", "dirname", "basename", "date", "sort",
            "cut", "tr", "wc", "tar", "gzip", "curl"], token).ConfigureAwait(false);
        await CopyPythonStandardLibraryAsync(layout, token).ConfigureAwait(false);
        CopyPublicConfiguration(layout);
        CopyNssLibraries(layout);
        var sdkPathFile = Path.Combine(layout.HostRoot, "sdk.hostpath");
        await File.WriteAllTextAsync(sdkPathFile, sdkDirectory + "\n", new UTF8Encoding(false, true), token).ConfigureAwait(false);
        SetOwner(sdkPathFile, 0, 0);
        SetMode(sdkPathFile, 0x180);
        OwnerCustody.VerifyRootHost(layout.HostRoot);
    }

    private static async Task CopyBinaryClosureAsync(OwnerLayout layout, IEnumerable<string> names, CancellationToken token)
    {
        var binaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var candidate = new[] { "/usr/bin/" + name, "/bin/" + name, "/usr/sbin/" + name, "/sbin/" + name }
                .FirstOrDefault(File.Exists);
            if (candidate is null) continue;
            var resolved = Path.GetFullPath(new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate);
            binaries.Add(Path.GetFullPath(candidate));
            binaries.Add(resolved);
            var ldd = await RunFixedAsync("/usr/bin/ldd", [resolved], 64 * 1024, 64 * 1024, TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);
            Require(ldd.ExitCode == 0, "trusted-binary-dependencies");
            AddDependencies(Encoding.UTF8.GetString(ldd.StandardOutput), binaries);
        }
        foreach (var binary in binaries)
        {
            token.ThrowIfCancellationRequested();
            CopyRootFile(binary, layout.Host(binary), executable: true);
        }
    }

    private static async Task CopyPythonStandardLibraryAsync(OwnerLayout layout, CancellationToken token)
    {
        var source = "/usr/lib/python3.12";
        Require(Directory.Exists(source), "python312-stdlib");
        var extensions = new List<string>();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, path);
            Require(!relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "." or ".."), "stdlib-path");
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
            var destination = layout.Host("/usr/lib/python3.12/" + relative.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyRootFile(path, destination, executable: path.EndsWith(".so", StringComparison.Ordinal));
            if (path.EndsWith(".so", StringComparison.Ordinal)) extensions.Add(path);
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            var destination = layout.Host("/usr/lib/python3.12/" + relative.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(destination);
            SetOwner(destination, 0, 0);
            SetMode(destination, 0x1ed);
        }
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            token.ThrowIfCancellationRequested();
            var ldd = await RunFixedAsync("/usr/bin/ldd", [extension], 64 * 1024, 64 * 1024,
                TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            Require(ldd.ExitCode == 0, "stdlib-extension-dependencies");
            AddDependencies(Encoding.UTF8.GetString(ldd.StandardOutput), dependencies);
        }
        foreach (var dependency in dependencies)
        {
            token.ThrowIfCancellationRequested();
            CopyRootFile(dependency, layout.Host(dependency), executable: true);
        }
    }

    private static void CopyPublicConfiguration(OwnerLayout layout)
    {
        var uid = layout.Uid;
        var passwd = $"root:x:0:0:root:/root:/bin/sh\nrunner:x:{uid}:{uid}:runner:/home/runner:/bin/sh\nnobody:x:65534:65534:nobody:/nonexistent:/bin/false\n";
        var group = $"root:x:0:\nrunner:x:{uid}:\nnogroup:x:65534:\n";
        var configs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/etc/passwd"] = passwd,
            ["/etc/group"] = group,
            ["/etc/nsswitch.conf"] = "passwd: files\ngroup: files\nhosts: files dns\n",
            ["/etc/hosts"] = "127.0.0.1 localhost\n::1 localhost\n",
            ["/etc/resolv.conf"] = "nameserver 1.1.1.1\nnameserver 8.8.8.8\noptions timeout:2 attempts:2\n",
            ["/etc/ssl/certs/ca-certificates.crt"] = "",
        };
        foreach (var pair in configs)
        {
            var path = layout.Host(pair.Key);
            if (pair.Key.EndsWith("ca-certificates.crt", StringComparison.Ordinal))
            {
                var ca = "/etc/ssl/certs/ca-certificates.crt";
                if (File.Exists(ca)) CopyRootFile(ca, path, executable: false);
                continue;
            }
            File.WriteAllText(path, pair.Value, new UTF8Encoding(false, true));
            SetOwner(path, 0, 0);
            SetMode(path, 0x1a4);
        }
    }

    private static void CopyNssLibraries(OwnerLayout layout)
    {
        foreach (var name in new[] { "libnss_dns.so.2", "libnss_files.so.2" })
        {
            var candidates = new[]
            {
                "/lib/x86_64-linux-gnu/" + name,
                "/usr/lib/x86_64-linux-gnu/" + name,
                "/lib64/" + name,
            };
            var path = candidates.FirstOrDefault(File.Exists);
            Require(path is not null, "nss-library-missing");
            CopyRootFile(path!, layout.Host(path!), executable: true);
        }
    }

    private static async Task CopyOwnerAssemblyAsync(OwnerLayout layout, CancellationToken token)
    {
        var basePath = AppContext.BaseDirectory;
        var files = Directory.EnumerateFiles(basePath)
            .Where(static path => Path.GetExtension(path) is ".dll" or ".json")
            .Where(static path => Path.GetFileName(path).StartsWith("EvidenceHost.CompletedIsolation", StringComparison.Ordinal))
            .ToArray();
        Require(files.Any(static path => Path.GetFileName(path) == "EvidenceHost.CompletedIsolation.dll"), "owner-assembly");
        foreach (var source in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = layout.Host(OwnerPaths.Assembly.Replace("EvidenceHost.CompletedIsolation.dll", Path.GetFileName(source), StringComparison.Ordinal));
            await CopyFileBoundedAsync(source, destination, 16 * 1024 * 1024, token).ConfigureAwait(false);
            SetOwner(destination, 0, 0);
            SetMode(destination, 0x1a4);
        }
    }

    private static async Task WriteInputAsync(string path, byte[] bytes, uint uid, CancellationToken token)
    {
        Require(bytes.Length is > 0 and <= MaximumCaptureBytes, "input-write-bounds");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(true);
        }
        SetOwner(path, uid, uid);
        SetMode(path, 0x180);
    }

    private static async Task CopyFileBoundedAsync(string source, string destination, int limit, CancellationToken token)
    {
        var info = new FileInfo(source);
        Require(info.Exists && info.Length is > 0 && info.Length <= limit && !info.Attributes.HasFlag(FileAttributes.ReparsePoint),
            "copy-source-shape");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var amount = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (amount == 0) break;
            total += amount;
            Require(total <= limit, "copy-growth");
            await output.WriteAsync(buffer.AsMemory(0, amount), token).ConfigureAwait(false);
        }
        Require(total == info.Length, "copy-length");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static void CopyRootFile(string source, string destination, bool executable)
    {
        Require(File.Exists(source), "rootfs-source");
        var original = Path.GetFullPath(new FileInfo(source).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? source);
        Require(original.StartsWith("/usr/", StringComparison.Ordinal) || original.StartsWith("/bin/", StringComparison.Ordinal)
            || original.StartsWith("/sbin/", StringComparison.Ordinal) || original.StartsWith("/lib/", StringComparison.Ordinal)
            || original.StartsWith("/lib64/", StringComparison.Ordinal), "rootfs-source-path");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        SetOwner(destination, 0, 0);
        SetMode(destination, executable ? 0x1edu : 0x1a4u);
    }

    private static void AddDependencies(string lddOutput, ISet<string> dependencies)
    {
        foreach (var line in lddOutput.Split('\n'))
        {
            if (line.Contains("=> not found", StringComparison.Ordinal))
                throw new IsolationException("trusted-library-missing");
            var marker = line.IndexOf("=>", StringComparison.Ordinal);
            var dependency = marker >= 0 ? line[(marker + 2)..].Trim().Split(' ')[0] : line.Trim().Split(' ')[0];
            if (!dependency.StartsWith("/", StringComparison.Ordinal)) continue;
            var path = Path.GetFullPath(dependency);
            Require(path.StartsWith("/usr/", StringComparison.Ordinal) || path.StartsWith("/lib/", StringComparison.Ordinal)
                || path.StartsWith("/lib64/", StringComparison.Ordinal), "trusted-library-path");
            if (File.Exists(path)) dependencies.Add(path);
        }
    }

    private static void SetTreeOwner(string root, uint uid, uint gid)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "tree-symlink");
            SetOwner(path, uid, gid);
        }
    }

    private static Task SetTreeOwnerAsync(string root, uint uid, uint gid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SetTreeOwner(root, uid, gid);
        return Task.CompletedTask;
    }

    private static Task SetCheckoutOwnerAsync(string root, uint uid, uint gid, CancellationToken token)
    {
        var gitRoot = Path.Combine(root, ".git");
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root);
        foreach (var path in entries)
        {
            token.ThrowIfCancellationRequested();
            Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "checkout-symlink");
            var relative = Path.GetRelativePath(root, path);
            var inGitMetadata = relative == ".git" || relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (inGitMetadata)
            {
                var info = OwnerCustody.StatPath(path);
                Require(info.Uid == 0 && info.Gid == 0 && (info.Mode & 0x12) == 0, "git-metadata-owner");
            }
            else SetOwner(path, uid, gid);
        }
        Require(Directory.Exists(gitRoot), "checkout-gitdir");
        return Task.CompletedTask;
    }

    internal static void SetOwner(string path, uint uid, uint gid)
    {
        if (Chown(path, uid, gid) != 0) throw new IsolationException("ownership-change");
    }

    internal static void SetMode(string path, uint mode)
    {
        if (Chmod(path, mode) != 0) throw new IsolationException("mode-change");
    }

    private static void TryQuarantine(string hostRoot)
    {
        try
        {
            var quarantine = hostRoot + ".quarantine";
            if (!Directory.Exists(quarantine) && Directory.Exists(hostRoot)) Directory.Move(hostRoot, quarantine);
        }
        catch { }
    }

    private static bool TryUnmount(string path)
    {
        try
        {
            return RunFixedAsync("/usr/bin/umount", [path], 4096, 4096, TimeSpan.FromSeconds(5), CancellationToken.None)
                .GetAwaiter().GetResult().ExitCode == 0;
        }
        catch { return false; }
    }

    private static void TryDetachLoop(string markerPath)
    {
        try
        {
            var line = OwnerCustody.ReadRootFile(markerPath, 0, 0x180, 128);
            var device = StrictSingleLine(line);
            _ = RunFixedAsync("/usr/sbin/losetup", ["--detach", device], 4096, 4096, TimeSpan.FromSeconds(5),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { }
    }

    private static void RemoveOwnedTree(string root)
    {
        var identity = OwnerCustody.StatPath(root);
        Require(identity.Type == OwnerCustody.DirectoryType && identity.Uid == 0 && identity.Gid == 0 && identity.Mode == 0x1c0,
            "cleanup-root-identity");
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "cleanup-link");
        Directory.Delete(root, recursive: true);
    }

    private static string StrictSingleLine(byte[] bytes)
    {
        var value = new UTF8Encoding(false, true).GetString(bytes);
        var trimmed = value.Trim();
        Require(trimmed.Length is > 0 and <= 256 && !trimmed.Contains('\n') && !trimmed.Contains('\r'), "utility-single-line");
        return trimmed;
    }

    private static SafeFileHandle OpenExistingMarker(string path)
    {
        var fd = OpenExclusive(path, 0x80000 | 0x20000, 0);
        if (fd < 0) throw new IsolationException("uid-marker-open");
        return new SafeFileHandle((IntPtr)fd, true);
    }

    private static void Require(bool condition, string category)
    {
        if (!condition) throw new IsolationException(category);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenExclusive(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)] private static extern int Chown(string path, uint owner, uint group);
    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)] private static extern int Chmod(string path, uint mode);
}
