using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Fixed hostile probes executed inside the same phase containment as the real replay.</summary>
/// <remarks>These use synthetic credentials and disposable files. Success grants no protected provenance or admission.</remarks>
[SupportedOSPlatform("linux")]
internal static class ControlProbe
{
    internal static readonly string[] Names = ["baseline", "readonly-writes", "credential-denial", "spawn-escape",
        "ignore-stop", "output-overflow", "quota", "mutate-source"];

    internal static async Task<int> RunAsync(string control)
    {
        if (!Names.Contains(control, StringComparer.Ordinal)) throw new IsolationException("control-name");
        switch (control)
        {
            case "baseline":
                Require(Environment.GetEnvironmentVariables().Count == 4, "environment-count");
                Require(Directory.Exists(OwnerPaths.Source) && Directory.Exists(OwnerPaths.Native), "source-visible");
                Require(File.ReadAllText(OwnerPaths.Input + "/immutable-canary") == "immutable\n", "input-visible");
                Require(File.ReadAllText(OwnerPaths.Stage + "/archive-canary") == "archive\n", "archive-visible");
                Require(File.ReadAllText(OwnerPaths.Preparation) == "comparison-only\n", "preparation-visible");
                foreach (var path in new[] { OwnerPaths.Source + "/obj/isolation-control", OwnerPaths.Native + "/obj/isolation-control",
                    OwnerPaths.Output + "/write-control", OwnerPaths.Handoff + "/sibling-control", "/tmp/write-control" })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, "owned\n");
                    File.Delete(path);
                }
                var sdkProbe = new ProcessStartInfo(OwnerPaths.Dotnet, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                // The SDK's own first-use state belongs in private scratch. The phase/worker
                // itself still has exactly the four credential-free environment entries.
                sdkProbe.Environment["DOTNET_CLI_HOME"] = "/tmp/sdk-control-home";
                using (var process = Process.Start(sdkProbe)!)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    Require(process.ExitCode == 0 && output.Trim() == "10.0.401", "sdk-version");
                }
                break;
            case "readonly-writes":
                foreach (var original in new[] { OwnerPaths.Validator + "/README.md", OwnerPaths.SourceCapture,
                    OwnerPaths.Input + "/immutable-canary", OwnerPaths.Stage + "/archive-canary", OwnerPaths.Preparation,
                    "/etc/passwd", OwnerPaths.Assembly, OwnerPaths.Source + "/.git/config", OwnerPaths.Native + "/.git/config" })
                    Require(File.Exists(original) && new FileInfo(original).Length > 0, "immutable-present");
                foreach (var path in new[] { OwnerPaths.Validator + "/README.md", OwnerPaths.SourceCapture,
                    OwnerPaths.Input + "/immutable-canary", OwnerPaths.Stage + "/archive-canary", OwnerPaths.Preparation,
                    "/etc/passwd", OwnerPaths.Assembly, OwnerPaths.Source + "/.git/config", OwnerPaths.Native + "/.git/config" })
                {
                    Denied(() => { using var file = new FileStream(path, FileMode.Open, FileAccess.Write); }, "immutable-write");
                }
                foreach (var path in new[] { OwnerPaths.Preparation, OwnerPaths.SourceCapture, OwnerPaths.Stage + "/archive-canary" })
                    ReadOnlyRenameProbe.RequireDenied(path, path + ".replaced");
                Denied(() => File.WriteAllText("/etc/unexpected", "deny"), "system-write");
                break;
            case "credential-denial":
                Require(Environment.GetEnvironmentVariable("GH_TOKEN") is null, "token-environment");
                var hostCanaryPath = File.ReadAllText(OwnerPaths.Input + "/host-canary-path").Trim();
                Require(hostCanaryPath.StartsWith("/var/lib/appsurface-completed-", StringComparison.Ordinal)
                    && hostCanaryPath.EndsWith("/credential-canary", StringComparison.Ordinal)
                    && !File.Exists(hostCanaryPath), "host-credential-hidden");
                try
                {
                    using var hostCanary = File.OpenRead(hostCanaryPath);
                    throw new IsolationException("host-credential-readable");
                }
                catch (UnauthorizedAccessException) { }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }

                Require(!Directory.Exists("/run/systemd") && !File.Exists("/run/dbus/system_bus_socket"), "manager-hidden");
                // The minimal namespace keeps an empty root-only directory. A denied traversal
                // is the expected isolation result for the dedicated non-root identity.
                if (Directory.Exists("/root"))
                {
                    try { Require(!Directory.EnumerateFileSystemEntries("/root").Any(), "root-home-hidden"); }
                    catch (UnauthorizedAccessException) { }
                }
                Require(!Directory.Exists("/home/runner/.config") && !Directory.Exists("/home/runner/.ssh"), "runner-home-hidden");
                var pids = Directory.GetDirectories("/proc").Select(Path.GetFileName)
                    .Where(name => uint.TryParse(name, out _)).ToArray();
                Require(pids.Length <= 4, "private-proc");
                Require(File.ReadAllText("/proc/1/status").Contains("NoNewPrivs:\t1", StringComparison.Ordinal), "private-init");
                foreach (var pid in pids)
                {
                    try
                    {
                        var bytes = File.ReadAllBytes("/proc/" + pid + "/environ");
                        Require(!Encoding.UTF8.GetString(bytes).Contains("GH_TOKEN=", StringComparison.Ordinal), "token-process");
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (FileNotFoundException) { }
                }
                break;
            case "spawn-escape":
                return await SpawnEscapeAsync();
            case "ignore-stop":
                using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true))
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                break;
            case "output-overflow":
                var line = new string('x', 4096);
                while (true) await Console.Out.WriteLineAsync(line);
            case "quota":
                // Both byte and inode pressure are observed by real allocation failures on the bounded image.
                var quotaPath = OwnerPaths.Output + "/quota-control";
                var bytesWritten = 0L;
                try
                {
                    using var file = new FileStream(quotaPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var block = new byte[1024 * 1024];
                    while (bytesWritten <= 8L * 1024 * 1024 * 1024)
                    {
                        await file.WriteAsync(block);
                        bytesWritten += block.Length;
                    }
                    throw new IsolationException("byte-quota-unbounded");
                }
                catch (IOException) { Require(bytesWritten < 8L * 1024 * 1024 * 1024, "byte-quota"); }
                finally { File.Delete(quotaPath); }
                var directory = OwnerPaths.Output + "/inode-control";
                Directory.CreateDirectory(directory);
                var created = 0;
                try
                {
                    for (; created <= 131072; created++)
                        using (File.Create(directory + "/" + created.ToString(System.Globalization.CultureInfo.InvariantCulture))) { }
                    throw new IsolationException("inode-quota-unbounded");
                }
                catch (IOException) { Require(created < 131072, "inode-quota"); }
                finally { Directory.Delete(directory, recursive: true); }
                break;
            case "mutate-source":
                await File.AppendAllTextAsync(OwnerPaths.Source + "/README.md", "\nISOLATION_TAMPER_CONTROL\n");
                break;
        }
        Console.WriteLine("ISOLATION_CONTROL_PASSED:" + control);
        return 0;
    }

    private static async Task<int> SpawnEscapeAsync()
    {
        // A fixed Python mechanism probe is target code inside containment, never a privileged launcher.
        var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false };
        foreach (var value in new[] { "-I", "-S", "-B", "-c",
            "import os,signal,time,pathlib; p=os.fork();\nif p==0:\n os.setsid(); q=os.fork();\n if q==0:\n  signal.signal(signal.SIGTERM,signal.SIG_IGN); pathlib.Path('/opt/appsurface/output/escaped-child').write_text(str(os.getpid())); time.sleep(300)\n os._exit(0)\nos.waitpid(p,0)\ndeadline=time.monotonic()+5\nwhile not pathlib.Path('/opt/appsurface/output/escaped-child').exists() and time.monotonic()<deadline: time.sleep(.01)\nassert pathlib.Path('/opt/appsurface/output/escaped-child').exists()\ntime.sleep(300)" }) start.ArgumentList.Add(value);
        start.Environment.Clear();
        foreach (var pair in new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/nonexistent", ["LANG"] = "C.UTF-8", ["LC_ALL"] = "C" })
            start.Environment.Add(pair.Key, pair.Value);
        using var process = Process.Start(start) ?? throw new IsolationException("escape-probe-start");
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "escape-probe-exit");
        Console.WriteLine("ISOLATION_CONTROL_PASSED:spawn-escape");
        return 0;
    }

    private static void Denied(Action action, string category)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException error) when ((error.HResult & 0xffff) is 13 or 30)
        { return; }
        throw new IsolationException(category);
    }

    private static void Require(bool condition, string category)
    {
        if (!condition) throw new IsolationException(category);
    }
}
