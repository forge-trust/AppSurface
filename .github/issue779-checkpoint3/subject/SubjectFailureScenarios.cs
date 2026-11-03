using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NativePreparation.Subject;

/// <summary>Fixed same-fixture controls for unsafe scratch custody, cleanup failure and ignored cancellation.</summary>
internal static class SubjectFailureScenarios
{
    private const int ReadinessTimeoutSeconds = 30;

    private const string IgnoringChildScript = """
        import os
        import signal

        tmp = os.environ["TMPDIR"]

        def write_marker(name, payload):
            path = os.path.join(tmp, name)
            pending = path + ".pending"
            fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            try:
                remaining = memoryview(payload)
                while remaining:
                    written = os.write(fd, remaining)
                    remaining = remaining[written:]
                os.fsync(fd)
            finally:
                os.close(fd)
            os.rename(pending, path)

        def on_term(signum, frame):
            try:
                write_marker("checkpoint2-term-observed", b"SIGTERM observed")
            except FileExistsError:
                pass

        signal.signal(signal.SIGTERM, on_term)
        write_marker("checkpoint2-ignored-child-pid", str(os.getpid()).encode("ascii"))
        write_marker("checkpoint2-case-entered", b"ignored-cancel")
        write_marker("checkpoint2-child-ready", b"SIGTERM handler installed")
        while True:
            signal.pause()
        """;

    /// <summary>Executes only the fixed root-selected negative fixture branches.</summary>
    internal static async Task Run(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        switch (kind)
        {
            case "scratch-fifo":
            case "scratch-hardlink":
            case "scratch-oversize":
                CreateUnsafeScratchNode(kind);
                break;
            case "cleanup-failure":
                CreateCleanupFailure();
                break;
            case "ignored-cancel":
                await RunIgnoredCancellationAsync().ConfigureAwait(false);
                break;
            case "normal":
            case "test-failure":
            case "cancel":
            case "quota":
                break;
            default:
                throw new InvalidOperationException("Unknown root-selected fixture scenario.");
        }
    }

    private static void CreateUnsafeScratchNode(string kind)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The scratch-node controls require Linux.");
        var (_, temporary) = GetScratchPaths();
        var path = Path.Combine(temporary, "checkpoint3-" + kind);
        if (kind == "scratch-fifo")
        {
            if (MkFifo(path, 0x180) != 0)
                throw new IOException("The fixed scratch FIFO could not be created.");
            return; // No writer is opened: the owner must reject type before a potentially blocking read.
        }
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (kind == "scratch-oversize") file.SetLength(256L * 1024 * 1024 + 1);
            else file.Write("owned scratch bytes"u8);
            file.Flush(flushToDisk: true);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (kind == "scratch-hardlink" && Link(path, path + "-linked") != 0)
            throw new IOException("The fixed scratch hard link could not be created.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string original,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string linked);

    private static void CreateCleanupFailure()
    {
        var (home, temporary) = GetScratchPaths();
        var suffix = Guid.NewGuid().ToString("N");
        var target = Path.Combine(home, "checkpoint2-owned-target-" + suffix);
        var link = Path.Combine(temporary, "checkpoint2-cleanup-link-" + suffix);

        Directory.CreateDirectory(target);
        using (var owned = new FileStream(Path.Combine(target, "owned.txt"), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            var bytes = "owned scratch target"u8;
            owned.Write(bytes);
            owned.Flush(flushToDisk: true);
        }

        Directory.CreateSymbolicLink(link, target);
    }

    private static async Task RunIgnoredCancellationAsync()
    {
        var (_, temporary) = GetScratchPaths();
        var readyMarker = Path.Combine(temporary, "checkpoint2-child-ready");
        var childPidMarker = Path.Combine(temporary, "checkpoint2-ignored-child-pid");
        var enteredMarker = Path.Combine(temporary, "checkpoint2-case-entered");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/python3")
            {
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = temporary
            }
        };
        child.StartInfo.ArgumentList.Add("-c");
        child.StartInfo.ArgumentList.Add(IgnoringChildScript);

        if (!child.Start()) throw new InvalidOperationException("The fixed descendant did not start.");
        try
        {
            await WaitForChildReadinessAsync(child, temporary, readyMarker, childPidMarker, enteredMarker)
                .ConfigureAwait(false);
        }
        catch
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().ConfigureAwait(false);
            }

            throw;
        }

        // Await the real descendant lifetime; the external control owns cancellation and cgroup settlement.
        await child.WaitForExitAsync().ConfigureAwait(false);
    }

    private static async Task WaitForChildReadinessAsync(
        Process child,
        string directory,
        string readyMarker,
        string childPidMarker,
        string enteredMarker)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var markerName = Path.GetFileName(readyMarker);
        using var watcher = new FileSystemWatcher(directory, markerName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => ready.TrySetResult(true);
        watcher.Renamed += (_, args) =>
        {
            if (StringComparer.Ordinal.Equals(args.Name, markerName)) ready.TrySetResult(true);
        };

        // Markers are atomically renamed after the child installs its SIGTERM handler.
        if (File.Exists(readyMarker)) ready.TrySetResult(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ReadinessTimeoutSeconds));
        var childExited = child.WaitForExitAsync();
        var completed = await Task.WhenAny(ready.Task, childExited).WaitAsync(timeout.Token).ConfigureAwait(false);
        if (completed == childExited)
            throw new InvalidOperationException("The fixed descendant exited before readiness.");
        await ready.Task.ConfigureAwait(false);

        if (!StringComparer.Ordinal.Equals(File.ReadAllText(readyMarker), "SIGTERM handler installed"))
            throw new InvalidOperationException("The fixed descendant readiness marker changed.");
        if (!int.TryParse(File.ReadAllText(childPidMarker), NumberStyles.None, CultureInfo.InvariantCulture,
                out var publishedPid)
            || publishedPid <= 0 || publishedPid != child.Id)
            throw new InvalidOperationException("The fixed descendant PID marker changed.");
        if (!StringComparer.Ordinal.Equals(File.ReadAllText(enteredMarker), "ignored-cancel"))
            throw new InvalidOperationException("The fixed descendant case marker changed.");
    }

    private static (string Home, string Temporary) GetScratchPaths()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var temporary = Environment.GetEnvironmentVariable("TMPDIR");
        if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(temporary)
            || !Path.IsPathFullyQualified(home) || !Path.IsPathFullyQualified(temporary))
            throw new InvalidOperationException("The fixture scratch paths are unavailable.");

        home = Path.GetFullPath(home);
        temporary = Path.GetFullPath(temporary);
        if (!StringComparer.Ordinal.Equals(temporary, Path.Combine(home, "tmp"))
            || !Directory.Exists(home) || !Directory.Exists(temporary))
            throw new InvalidOperationException("The fixture scratch paths do not match the prepared workspace.");
        return (home, temporary);
    }
}
